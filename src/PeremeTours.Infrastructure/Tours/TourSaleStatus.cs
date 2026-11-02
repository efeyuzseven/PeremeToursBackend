using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using PeremeTours.Application.Tours;

namespace PeremeTours.Infrastructure.Tours;

// Decides which tours the public tour list offers: the same rule the booking drawer uses to show
// departures (a TL price and a departure still ahead in Istanbul time), so a listed tour can be booked.
internal sealed class TourSaleStatus(
    ITourCatalogService catalog,
    IMemoryCache cache,
    TimeProvider timeProvider
) : ITourSaleStatus
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeZoneInfo IstanbulTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public async Task<bool> IsOnSaleAsync(int externalTourId, CancellationToken cancellationToken)
    {
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"tours:on-sale:v1:{externalTourId}");
        if (cache.TryGetValue(cacheKey, out bool cached))
        {
            return cached;
        }

        bool onSale;
        try
        {
            onSale = await HasBookableDepartureAsync(externalTourId, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            onSale = false;
        }
        catch (TourCatalogUnavailableException)
        {
            // A provider outage must not empty the site; the drawer reports the outage itself.
            return true;
        }

        cache.Set(cacheKey, onSale, CacheDuration);
        return onSale;
    }

    private async Task<bool> HasBookableDepartureAsync(int externalTourId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var port in await catalog.ListPortsAsync(externalTourId, cancellationToken))
        {
            var availability = await catalog.GetAvailabilityAsync(externalTourId, port.ExternalPortId, 2, cancellationToken);
            if (availability is not null
                && availability.Prices.Any(IsTryPrice)
                && availability.Departures.Any(departure => IsUpcoming(departure, now)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTryPrice(TourPrice price) =>
        price.ExternalPriceId > 0 && price.Amount >= 0
        && (string.Equals(price.Currency, "TRY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(price.Currency, "TL", StringComparison.OrdinalIgnoreCase));

    private static bool IsUpcoming(TourDeparture departure, DateTime nowUtc) =>
        departure.ExternalId > 0 && departure.Date is { } date && departure.Time is { } time
        && TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), IstanbulTimeZone) > nowUtc;
}
