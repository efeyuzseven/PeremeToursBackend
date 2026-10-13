using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Email;

internal sealed class CancellationEmailProcessor(PeremeToursDbContext db, ICancellationEmailSender sender, TimeProvider clock)
{
    private const int MaximumAttempts = 4;

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var staleId = await db.CancellationEmails.AsNoTracking()
            .Where(item => item.Status == PaymentEmailStatus.Processing && item.LockedUntilUtc < now)
            .Select(item => (Guid?)item.TicketId).FirstOrDefaultAsync(cancellationToken);
        if (staleId is { } stale)
        {
            var changed = await db.CancellationEmails.Where(item => item.TicketId == stale
                    && item.Status == PaymentEmailStatus.Processing && item.LockedUntilUtc < now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, PaymentEmailStatus.ReviewRequired)
                    .SetProperty(item => item.LastFailureCode, "CANCELLATION_EMAIL_SMTP_RESULT_UNKNOWN")
                    .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), cancellationToken);
            if (changed > 0)
            {
                RecordFailure(stale, "CANCELLATION_EMAIL_SMTP_RESULT_UNKNOWN");
                await db.SaveChangesAsync(cancellationToken);
            }
            return true;
        }
        var id = await db.CancellationEmails.AsNoTracking().Where(item => item.Status == PaymentEmailStatus.Queued
                && item.NextAttemptAtUtc <= now).OrderBy(item => item.NextAttemptAtUtc)
            .Select(item => (Guid?)item.TicketId).FirstOrDefaultAsync(cancellationToken);
        if (id is null) return false;
        var lockToken = Guid.NewGuid();
        var claimed = await db.CancellationEmails.Where(item => item.TicketId == id.Value
                && item.Status == PaymentEmailStatus.Queued && item.NextAttemptAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, PaymentEmailStatus.Processing)
                .SetProperty(item => item.LockToken, lockToken).SetProperty(item => item.LockedUntilUtc, now.AddMinutes(3))
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), cancellationToken);
        if (claimed != 1) return true;
        // Fresh snapshots avoid an earlier scoped/tracked entity masking the atomic claim or bank state.
        var email = await db.CancellationEmails.AsNoTracking().Include(item => item.Ticket).ThenInclude(item => item.Cancellation)
            .SingleAsync(item => item.TicketId == id.Value, cancellationToken);
        if (email.Status != PaymentEmailStatus.Processing || email.LockToken != lockToken) return true;
        if (!CancellationEmailPolicy.IsConfirmed(email.Ticket))
        {
            email.Status = PaymentEmailStatus.Failed;
            email.LastFailureCode = "CANCELLATION_EMAIL_NOT_CONFIRMED";
        }
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await sender.SendAsync(email, timeout.Token);
                email.Status = PaymentEmailStatus.Sent;
                email.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                email.LastFailureCode = null;
            }
            catch (EmailDeliveryException exception)
            {
                var code = PaymentDiagnostics.SafeCode("CANCELLATION_EMAIL_" + exception.Code, "CANCELLATION_EMAIL_SMTP_RESULT_UNKNOWN");
                var ambiguous = exception.IsAmbiguous || code == "CANCELLATION_EMAIL_SMTP_RESULT_UNKNOWN";
                email.Status = ambiguous ? PaymentEmailStatus.ReviewRequired
                    : exception.CanRetry && email.AttemptCount < MaximumAttempts ? PaymentEmailStatus.Queued : PaymentEmailStatus.Failed;
                email.LastFailureCode = code;
                email.NextAttemptAtUtc = now.AddSeconds(60 * email.AttemptCount);
            }
            catch
            {
                email.Status = PaymentEmailStatus.ReviewRequired;
                email.LastFailureCode = "CANCELLATION_EMAIL_SMTP_RESULT_UNKNOWN";
            }
        }
        // Complete only OUR lease; SMTP acknowledgement and diagnostics survive application shutdown.
        // Only email/error records are written here, never the ticket, provider or bank ledger.
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var finalized = await db.CancellationEmails.Where(item => item.TicketId == email.TicketId
                && item.Status == PaymentEmailStatus.Processing && item.LockToken == lockToken)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, email.Status)
                .SetProperty(item => item.SentAtUtc, email.SentAtUtc).SetProperty(item => item.LastFailureCode, email.LastFailureCode)
                .SetProperty(item => item.NextAttemptAtUtc, email.NextAttemptAtUtc)
                .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), CancellationToken.None);
        if (finalized != 1) return true;
        if (email.LastFailureCode is { } failure) RecordFailure(email.TicketId, failure);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private void RecordFailure(Guid id, string code) => PaymentDiagnostics.Add(db, id, TicketErrorStage.Email, code,
        code == "CANCELLATION_EMAIL_NOT_CONFIRMED"
            ? "İptal maili gönderilmedi: banka onaylı iptal/iade kaydı doğrulanamadı. Banka işlemi tekrarlanmadı."
            : "İptal/iade maili gönderimi tamamlanamadı veya sonucu belirsiz. Mail sunucusu kaydı kontrol edilmeli; banka işlemi tekrarlanmadı.", clock.GetUtcNow());
}

internal sealed class CancellationEmailWorker(IServiceScopeFactory scopes, IOptions<MailOptions> options,
    ILogger<CancellationEmailWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(3401, nameof(LogFailed)), "Cancellation email worker could not process its queue. No bank operation was replayed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<CancellationEmailProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { LogFailed(logger, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
