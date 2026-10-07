using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Email;

internal sealed class PaymentEmailProcessor(PeremeToursDbContext db, IPaymentEmailSender sender, TimeProvider clock)
{
    private const int MaximumAttempts = 4;

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // A worker may have crashed after SMTP accepted a message. Do not blindly resend stale claims.
        var staleId = await db.PaymentEmails.AsNoTracking()
            .Where(item => item.Status == PaymentEmailStatus.Processing && item.LockedUntilUtc < now)
            .Select(item => (Guid?)item.TicketId).FirstOrDefaultAsync(cancellationToken);
        if (staleId.HasValue)
        {
            var recovered = await db.PaymentEmails.Where(item => item.TicketId == staleId.Value
                    && item.Status == PaymentEmailStatus.Processing && item.LockedUntilUtc < now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, PaymentEmailStatus.ReviewRequired)
                    .SetProperty(item => item.LastFailureCode, "SMTP_RESULT_UNKNOWN")
                    .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), cancellationToken);
            if (recovered > 0)
            {
                PaymentDiagnostics.Add(db, staleId.Value, TicketErrorStage.Email, "SMTP_RESULT_UNKNOWN",
                    "Mail işleyicisi kesildi; gönderim sonucu belirsiz. Tekrar göndermeden önce SMTP kaydı kontrol edilmeli.", clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }
            return true;
        }
        var id = await db.PaymentEmails.AsNoTracking().Where(item => item.Status == PaymentEmailStatus.Queued
                && item.NextAttemptAtUtc <= now && item.Ticket.PaymentStatus == TicketPaymentStatus.Paid
                && item.Ticket.Status != TicketStatus.Cancelled && item.Ticket.Cancellation == null)
            .OrderBy(item => item.NextAttemptAtUtc).Select(item => (Guid?)item.TicketId).FirstOrDefaultAsync(cancellationToken);
        if (!id.HasValue) return false;
        var token = Guid.NewGuid();
        var claimed = await db.PaymentEmails.Where(item => item.TicketId == id.Value
                && item.Status == PaymentEmailStatus.Queued && item.NextAttemptAtUtc <= now
                && item.Ticket.PaymentStatus == TicketPaymentStatus.Paid && item.Ticket.Status != TicketStatus.Cancelled && item.Ticket.Cancellation == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, PaymentEmailStatus.Processing)
                .SetProperty(item => item.LockToken, token).SetProperty(item => item.LockedUntilUtc, now.AddMinutes(3))
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), cancellationToken);
        if (claimed == 0) return true;
        var email = await db.PaymentEmails.Include(item => item.Ticket).ThenInclude(item => item.Passengers)
            .SingleAsync(item => item.TicketId == id.Value, cancellationToken);
        if (email.Ticket.TicketingStatus is TicketingStatus.Pending or TicketingStatus.Processing
            && now - email.CreatedAtUtc < TimeSpan.FromMinutes(5))
        {
            email.Status = PaymentEmailStatus.Queued;
            email.AttemptCount--;
            email.NextAttemptAtUtc = now.AddSeconds(10);
        }
        else
        {
            try
            {
                using var deliveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deliveryTimeout.CancelAfter(TimeSpan.FromSeconds(90));
                await sender.SendAsync(email.Ticket, deliveryTimeout.Token);
                email.Status = PaymentEmailStatus.Sent;
                email.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                email.LastFailureCode = null;
            }
            catch (EmailDeliveryException exception)
            {
                email.Status = exception.IsAmbiguous ? PaymentEmailStatus.ReviewRequired
                    : exception.CanRetry && email.AttemptCount < MaximumAttempts ? PaymentEmailStatus.Queued : PaymentEmailStatus.Failed;
                email.LastFailureCode = exception.Code;
                email.NextAttemptAtUtc = now.AddSeconds(60 * email.AttemptCount);
                PaymentDiagnostics.Add(db, email.TicketId, TicketErrorStage.Email, exception.Code, exception.Message, clock.GetUtcNow());
            }
            catch
            {
                email.Status = PaymentEmailStatus.ReviewRequired;
                email.LastFailureCode = "SMTP_RESULT_UNKNOWN";
                PaymentDiagnostics.Add(db, email.TicketId, TicketErrorStage.Email, "SMTP_RESULT_UNKNOWN",
                    "Mail gönderim sonucu doğrulanamadı. Yeni tahsilat yapmayın; SMTP kaydı kontrol edilmeli.", clock.GetUtcNow());
            }
        }
        email.LockToken = null;
        email.LockedUntilUtc = null;
        // Persist even if the application is stopping: an acknowledged send must not re-enter the queue.
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }
}

internal sealed class PaymentEmailWorker(IServiceScopeFactory scopes, IOptions<MailOptions> options,
    ILogger<PaymentEmailWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(3201, nameof(LogFailed)), "Pereme payment email worker could not process its queue. No payment retry was performed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<PaymentEmailProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { LogFailed(logger, null); } // Never log SMTP exception messages or credentials.
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
