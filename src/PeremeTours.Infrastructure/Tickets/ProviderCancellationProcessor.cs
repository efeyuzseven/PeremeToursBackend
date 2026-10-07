using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.Infrastructure.Tickets;

// Independent of the bank leg: only bookings with a durably confirmed refund
// enter this queue. Provider problems are recorded internally, never returned as
// a failed bank refund or used as a reason to send a second bank operation.
internal sealed class ProviderCancellationProcessor(PeremeToursDbContext db, IEasyTicketCancellationGateway provider, TimeProvider clock)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var stale = await db.TicketCancellations.AsNoTracking()
            .Where(item => item.ProviderStatus == ProviderCancellationStatus.Processing && item.ProviderLockedUntilUtc < now)
            .Select(item => (Guid?)item.TicketId).FirstOrDefaultAsync(cancellationToken);
        if (stale.HasValue)
        {
            var changed = await db.TicketCancellations.Where(item => item.TicketId == stale.Value
                    && item.ProviderStatus == ProviderCancellationStatus.Processing && item.ProviderLockedUntilUtc < now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ProviderStatus, ProviderCancellationStatus.ReviewRequired)
                    .SetProperty(item => item.ProviderFailureCode, "PROVIDER_CANCELLATION_UNKNOWN")
                    .SetProperty(item => item.ProviderLockedUntilUtc, (DateTime?)null), cancellationToken);
            if (changed == 1)
            {
                PaymentDiagnostics.Add(db, stale.Value, TicketErrorStage.Cancellation, "PROVIDER_CANCELLATION_UNKNOWN",
                    "Banka iadesi onaylandı. EasyTicket iptal işleyicisi kesildi; bilet iptali sonucu iç kayıttan kontrol edilmeli, otomatik tekrar gönderilmez.", clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }
            return true;
        }
        var id = await db.TicketCancellations.AsNoTracking().Where(item => item.ProviderStatus == ProviderCancellationStatus.Queued
                && item.ProviderNextAttemptAtUtc <= now && item.Status == TicketCancellationStatus.Completed
                && item.Ticket.PaymentStatus == TicketPaymentStatus.Refunded && item.Ticket.Status == TicketStatus.Cancelled)
            .OrderBy(item => item.ProviderNextAttemptAtUtc).Select(item => (Guid?)item.TicketId).FirstOrDefaultAsync(cancellationToken);
        if (!id.HasValue) return false;
        var claimed = await db.TicketCancellations.Where(item => item.TicketId == id.Value && item.ProviderStatus == ProviderCancellationStatus.Queued
                && item.ProviderNextAttemptAtUtc <= now && item.Status == TicketCancellationStatus.Completed
                && item.Ticket.PaymentStatus == TicketPaymentStatus.Refunded && item.Ticket.Status == TicketStatus.Cancelled)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ProviderStatus, ProviderCancellationStatus.Processing)
                .SetProperty(item => item.ProviderLockedUntilUtc, now.AddMinutes(3))
                .SetProperty(item => item.ProviderAttemptCount, item => item.ProviderAttemptCount + 1), cancellationToken);
        if (claimed != 1) return true;
        var record = await db.TicketCancellations.Include(item => item.Ticket).ThenInclude(item => item.Passengers)
            .SingleAsync(item => item.TicketId == id.Value, cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await provider.CancelAsync(record.Ticket, timeout.Token);
            record.ProviderStatus = ProviderCancellationStatus.Cancelled;
            record.ProviderCancelledAtUtc = clock.GetUtcNow().UtcDateTime;
            record.ProviderFailureCode = null;
            record.ProviderNextAttemptAtUtc = null;
        }
        catch (Exception exception)
        {
            record.ProviderFailureCode = exception is CancellationGatewayException known
                ? PaymentDiagnostics.SafeCode(known.Code, "PROVIDER_CANCELLATION_UNKNOWN") : "PROVIDER_CANCELLATION_UNKNOWN";
            // Only an unavailable read-only precheck can re-enter the queue.
            // A provider POST (500, timeout, malformed response, crash) is never replayed.
            var safeRetry = record.ProviderFailureCode == "PROVIDER_CANCELLATION_UNAVAILABLE" && record.ProviderAttemptCount < 3;
            record.ProviderStatus = safeRetry ? ProviderCancellationStatus.Queued : ProviderCancellationStatus.ReviewRequired;
            record.ProviderNextAttemptAtUtc = safeRetry ? clock.GetUtcNow().UtcDateTime.AddMinutes(record.ProviderAttemptCount) : null;
            PaymentDiagnostics.Add(db, record.TicketId, TicketErrorStage.Cancellation, record.ProviderFailureCode,
                "Banka iadesi onaylandı. EasyTicket bilet iptali tamamlanamadı; yalnızca iç hata kaydından takip edilir. Banka iadesi tekrar gönderilmez.", clock.GetUtcNow());
        }
        record.ProviderLockedUntilUtc = null;
        record.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }
}

internal sealed class ProviderCancellationWorker(IServiceScopeFactory scopes, IOptions<EasyTicketOptions> options,
    ILogger<ProviderCancellationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(3301, nameof(LogFailed)), "Pereme provider cancellation worker could not process its queue. No bank operation was replayed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<ProviderCancellationProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { LogFailed(logger, null); } // No raw provider responses, credentials or customer data.
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
