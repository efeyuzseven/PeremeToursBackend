using Microsoft.Extensions.Caching.Memory;

namespace PeremeTours.Infrastructure.Payments;

// Ephemeral, one-use bank HTML. No PAN/CVC is persisted. Serve on the API origin,
// not the frontend origin, so the bank can use its own cookies/XHR without access to the site's DOM/storage.
public sealed class ThreeDSecureFrameStore : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 8_000_000 });
    private readonly object _gate = new();

    public Guid Publish(string html)
    {
        var token = Guid.NewGuid();
        _cache.Set(token, html, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            Size = html.Length * 2L,
        });
        return token;
    }

    public string? Consume(Guid token)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue<string>(token, out var html)) return null;
            _cache.Remove(token);
            return html;
        }
    }

    public void Dispose() => _cache.Dispose();
}
