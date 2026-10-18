using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PeremeTours.Infrastructure.Payments;

public enum PaymentAttemptLimit
{
    None,
    Email,
    Card,
    SiteWide,
}

// Card-testing bots rotate IPs, so the per-IP rate limiter alone is not enough: also cap attempts per
// customer e-mail, per card and across the whole site.
public sealed class PaymentAttemptGuard(IOptions<PaymentProtectionOptions> options, TimeProvider timeProvider)
{
    private const int CleanupFrequency = 256;
    private const string SiteWideKey = "site";
    private static readonly TimeSpan SiteWideWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, AttemptWindow> _attempts = new();
    private readonly PaymentProtectionOptions _options = options.Value;
    private long _operationCount;

    public PaymentAttemptLimit TryAcquire(string? email, string? cardNumber, out TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow();
        if (Interlocked.Increment(ref _operationCount) % CleanupFrequency == 0) RemoveExpired(now);

        var identityWindow = TimeSpan.FromMinutes(Math.Max(1, _options.IdentityWindowMinutes));
        var normalizedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        var cardDigits = new string((cardNumber ?? string.Empty).Where(char.IsDigit).ToArray());

        // Per-identity limits run first so rejected attempts do not drain the site-wide budget.
        if (!TryAcquire("email:" + Hash(normalizedEmail), _options.AttemptsPerIdentity, identityWindow, now, out retryAfter))
            return PaymentAttemptLimit.Email;
        if (!TryAcquire("card:" + Hash(cardDigits), _options.AttemptsPerIdentity, identityWindow, now, out retryAfter))
            return PaymentAttemptLimit.Card;
        if (!TryAcquire(SiteWideKey, _options.PaymentsPerHour, SiteWideWindow, now, out retryAfter))
            return PaymentAttemptLimit.SiteWide;
        return PaymentAttemptLimit.None;
    }

    private bool TryAcquire(string key, int permitLimit, TimeSpan window, DateTimeOffset now, out TimeSpan retryAfter)
    {
        var current = _attempts.AddOrUpdate(
            key,
            _ => new AttemptWindow(now, window, 1),
            (_, existing) => now - existing.StartedAt >= existing.Length
                ? new AttemptWindow(now, window, 1)
                : existing with { Count = existing.Count + 1 });

        var allowed = current.Count <= permitLimit;
        var remaining = current.StartedAt + current.Length - now;
        retryAfter = allowed || remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        return allowed;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (key, value) in _attempts)
        {
            if (now - value.StartedAt >= value.Length) _attempts.TryRemove(new KeyValuePair<string, AttemptWindow>(key, value));
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record AttemptWindow(DateTimeOffset StartedAt, TimeSpan Length, int Count);
}
