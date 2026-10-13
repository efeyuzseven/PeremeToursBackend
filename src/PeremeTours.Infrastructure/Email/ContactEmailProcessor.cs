using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Email;

internal interface IContactEmailSender
{
    Task SendAsync(ContactEmail email, CancellationToken cancellationToken);
}

internal sealed class ContactEmailProcessor(PeremeToursDbContext db, IContactEmailSender sender, TimeProvider clock)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // An interrupted SMTP send is ambiguous: never automatically deliver it twice.
        var stale = await db.ContactEmails.Where(item => item.Status == ContactEmailStatus.Processing && item.LockedUntilUtc < now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.ReviewRequired)
                .SetProperty(item => item.LastFailureCode, "SMTP_RESULT_UNKNOWN")
                .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), cancellationToken);
        var id = await db.ContactEmails.AsNoTracking().Where(item => item.Status == ContactEmailStatus.Queued && item.NextAttemptAtUtc <= now)
            .OrderBy(item => item.NextAttemptAtUtc).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        if (id is null) return stale > 0;
        var token = Guid.NewGuid();
        var claimed = await db.ContactEmails.Where(item => item.Id == id && item.Status == ContactEmailStatus.Queued && item.NextAttemptAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.Processing)
                .SetProperty(item => item.LockToken, token).SetProperty(item => item.LockedUntilUtc, now.AddMinutes(3))
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1), cancellationToken);
        if (claimed != 1) return true;
        var email = await db.ContactEmails.AsNoTracking().Include(item => item.Request).SingleAsync(item => item.Id == id, cancellationToken);
        if (email.Status != ContactEmailStatus.Processing || email.LockToken != token) return true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await sender.SendAsync(email, timeout.Token);
            email.Status = ContactEmailStatus.Sent; email.SentAtUtc = clock.GetUtcNow().UtcDateTime;
            email.LastFailureCode = null;
        }
        catch (EmailDeliveryException exception)
        {
            email.Status = exception.IsAmbiguous ? ContactEmailStatus.ReviewRequired
                : exception.CanRetry && email.AttemptCount < 4 ? ContactEmailStatus.Queued : ContactEmailStatus.Failed;
            // Codes originate only in our sender, never from raw SMTP/customer content.
            email.LastFailureCode = exception.Code;
            email.NextAttemptAtUtc = now.AddSeconds(60 * email.AttemptCount);
        }
        catch { email.Status = ContactEmailStatus.ReviewRequired; email.LastFailureCode = "SMTP_RESULT_UNKNOWN"; }
        await db.ContactEmails.Where(item => item.Id == id && item.Status == ContactEmailStatus.Processing && item.LockToken == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, email.Status)
                .SetProperty(item => item.SentAtUtc, email.SentAtUtc).SetProperty(item => item.LastFailureCode, email.LastFailureCode)
                .SetProperty(item => item.NextAttemptAtUtc, email.NextAttemptAtUtc)
                .SetProperty(item => item.LockToken, (Guid?)null).SetProperty(item => item.LockedUntilUtc, (DateTime?)null), CancellationToken.None);
        return true;
    }
}

internal sealed class ContactEmailWorker(IServiceScopeFactory scopes, IOptions<MailOptions> options, ILogger<ContactEmailWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(3501, nameof(LogFailed)), "Contact email queue could not be processed. Request contents are not logged.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<ContactEmailProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { LogFailed(logger, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
