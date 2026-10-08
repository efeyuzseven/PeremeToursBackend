using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Email;

internal sealed class ReservationNotificationProcessor(PeremeToursDbContext db, IReservationNotificationSender sender, TimeProvider clock)
{
    private const int MaximumAttempts = 4;

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var staleId = await db.ReservationNotifications.AsNoTracking()
            .Where(item => item.Status == ReservationNotificationStatus.Processing && item.LockedUntilUtc < now)
            .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        if (staleId.HasValue)
        {
            var recovered = await db.ReservationNotifications.Where(item => item.Id == staleId.Value
                    && item.Status == ReservationNotificationStatus.Processing && item.LockedUntilUtc < now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ReservationNotificationStatus.ReviewRequired)
                    .SetProperty(item => item.LastFailureCode, "SMTP_RESULT_UNKNOWN")
                    .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), cancellationToken);
            if (recovered > 0)
            {
                var ticketId = await db.ReservationNotifications.Where(item => item.Id == staleId.Value).Select(item => item.TicketId).SingleAsync(cancellationToken);
                RecordFailure(ticketId, "SMTP_RESULT_UNKNOWN", "Gönderim işleyicisi kesildi; SMTP kaydı kontrol edilmeden tekrar gönderilmez.");
                await db.SaveChangesAsync(cancellationToken);
            }
            return true;
        }
        var skipped = await db.ReservationNotifications.Where(item => item.Status == ReservationNotificationStatus.Queued
                && (item.Ticket.Status == TicketStatus.Cancelled || item.Ticket.PaymentStatus == TicketPaymentStatus.Refunded || item.Ticket.Cancellation != null))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ReservationNotificationStatus.Skipped), cancellationToken);
        if (skipped > 0) return true;
        var id = await db.ReservationNotifications.AsNoTracking().Where(item => item.Status == ReservationNotificationStatus.Queued
                && item.NextAttemptAtUtc <= now && item.Ticket.PaymentStatus == TicketPaymentStatus.Paid
                && item.Ticket.TicketingStatus == TicketingStatus.Issued && item.Ticket.Status == TicketStatus.Confirmed && item.Ticket.Cancellation == null)
            .OrderBy(item => item.NextAttemptAtUtc).ThenBy(item => item.Id).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        if (!id.HasValue) return false;
        var token = Guid.NewGuid();
        var claimed = await db.ReservationNotifications.Where(item => item.Id == id.Value && item.Status == ReservationNotificationStatus.Queued
                && item.NextAttemptAtUtc <= now && item.Ticket.PaymentStatus == TicketPaymentStatus.Paid
                && item.Ticket.TicketingStatus == TicketingStatus.Issued && item.Ticket.Status == TicketStatus.Confirmed && item.Ticket.Cancellation == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ReservationNotificationStatus.Processing)
                .SetProperty(item => item.LockToken, token).SetProperty(item => item.LockedUntilUtc, now.AddMinutes(3))
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), cancellationToken);
        if (claimed == 0) return true;
        var notification = await db.ReservationNotifications.Include(item => item.Ticket).ThenInclude(item => item.Passengers)
            .Include(item => item.Ticket).ThenInclude(item => item.Cancellation).SingleAsync(item => item.Id == id.Value, cancellationToken);
        if (notification.Ticket.Cancellation is not null || notification.Ticket.Status != TicketStatus.Confirmed
            || notification.Ticket.PaymentStatus != TicketPaymentStatus.Paid || notification.Ticket.TicketingStatus != TicketingStatus.Issued)
            notification.Status = ReservationNotificationStatus.Skipped;
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await sender.SendAsync(notification, timeout.Token);
                notification.Status = ReservationNotificationStatus.Sent;
                notification.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                notification.LastFailureCode = null;
            }
            catch (EmailDeliveryException exception)
            {
                notification.Status = exception.IsAmbiguous ? ReservationNotificationStatus.ReviewRequired
                    : exception.CanRetry && notification.AttemptCount < MaximumAttempts ? ReservationNotificationStatus.Queued : ReservationNotificationStatus.Failed;
                notification.LastFailureCode = exception.Code;
                notification.NextAttemptAtUtc = now.AddSeconds(60 * notification.AttemptCount);
                RecordFailure(notification.TicketId, exception.Code, exception.Message);
            }
            catch
            {
                notification.Status = ReservationNotificationStatus.ReviewRequired;
                notification.LastFailureCode = "SMTP_RESULT_UNKNOWN";
                RecordFailure(notification.TicketId, "SMTP_RESULT_UNKNOWN", "Gönderim sonucu belirsiz; SMTP kaydı kontrol edilmeden tekrar gönderilmez.");
            }
        }
        notification.LockToken = null;
        notification.LockedUntilUtc = null;
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    private void RecordFailure(Guid ticketId, string code, string message) => PaymentDiagnostics.Add(db, ticketId, TicketErrorStage.Email,
        $"RESERVATION_{code}", $"İç rezervasyon bildirimi: {message}", clock.GetUtcNow());
}

internal sealed class ReservationNotificationWorker(IServiceScopeFactory scopes, IOptions<MailOptions> options,
    ILogger<ReservationNotificationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(3202, nameof(LogFailed)), "Pereme internal reservation notification worker could not process its queue. No payment retry was performed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<ReservationNotificationProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { LogFailed(logger, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
