using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Email;

internal interface IContactReplyEmailSender
{
    Task SendAsync(ContactReply reply, CancellationToken cancellationToken);
}

internal sealed class ContactReplyEmailProcessor(PeremeToursDbContext db, IContactReplyEmailSender sender, TimeProvider clock)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var stale = await db.ContactReplies.Where(item => item.Status == ContactEmailStatus.Processing && item.LockedUntilUtc < now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.ReviewRequired)
                .SetProperty(item => item.LastFailureCode, "SMTP_RESULT_UNKNOWN")
                .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), cancellationToken);
        var id = await db.ContactReplies.AsNoTracking().Where(item => item.Status == ContactEmailStatus.Queued && item.NextAttemptAtUtc <= now)
            .OrderBy(item => item.NextAttemptAtUtc).ThenBy(item => item.Id).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        if (id is null) return stale > 0;
        var token = Guid.NewGuid();
        var claimed = await db.ContactReplies.Where(item => item.Id == id && item.Status == ContactEmailStatus.Queued && item.NextAttemptAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.Processing)
                .SetProperty(item => item.LockToken, token).SetProperty(item => item.LockedUntilUtc, now.AddMinutes(3))
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), cancellationToken);
        if (claimed != 1) return true;
        var reply = await db.ContactReplies.AsNoTracking().Include(item => item.Request).SingleAsync(item => item.Id == id, cancellationToken);
        if (reply.Status != ContactEmailStatus.Processing || reply.LockToken != token) return true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await sender.SendAsync(reply, timeout.Token);
            reply.Status = ContactEmailStatus.Sent; reply.SentAtUtc = clock.GetUtcNow().UtcDateTime; reply.LastFailureCode = null;
        }
        catch (EmailDeliveryException exception)
        {
            reply.Status = exception.IsAmbiguous ? ContactEmailStatus.ReviewRequired
                : exception.CanRetry && reply.AttemptCount < 4 ? ContactEmailStatus.Queued : ContactEmailStatus.Failed;
            reply.LastFailureCode = exception.Code; reply.NextAttemptAtUtc = now.AddSeconds(60 * reply.AttemptCount);
        }
        catch { reply.Status = ContactEmailStatus.ReviewRequired; reply.LastFailureCode = "SMTP_RESULT_UNKNOWN"; }
        // Only our lease is finalized, even on shutdown. Nothing in this queue can mutate a ticket or bank operation.
        await db.ContactReplies.Where(item => item.Id == id && item.Status == ContactEmailStatus.Processing && item.LockToken == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, reply.Status)
                .SetProperty(item => item.SentAtUtc, reply.SentAtUtc).SetProperty(item => item.LastFailureCode, reply.LastFailureCode)
                .SetProperty(item => item.NextAttemptAtUtc, reply.NextAttemptAtUtc)
                .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), CancellationToken.None);
        return true;
    }
}

internal sealed class ContactReplyEmailWorker(IServiceScopeFactory scopes, IOptions<MailOptions> options, ILogger<ContactReplyEmailWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(3502, nameof(LogFailed)), "Feedback reply email queue could not be processed. Message contents are not logged.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<ContactReplyEmailProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { LogFailed(logger, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
