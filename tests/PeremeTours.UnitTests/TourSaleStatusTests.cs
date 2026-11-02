using Microsoft.Extensions.Caching.Memory;
using PeremeTours.Application.Tours;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.UnitTests;

public sealed class TourSaleStatusTests
{
    // 2026-10-26 12:00 in Istanbul (UTC+3).
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly TourPrice Adult = new(231, 1, "Yetişkin", 200, "TRY", 20, "Adult");

    [Fact]
    public async Task TourWithAnUpcomingDepartureOnAnyPortIsOnSale()
    {
        var catalog = new FakeCatalog
        {
            Ports = [new(3, "Kabataş", 1), new(2, "Beşiktaş", 2)],
            Availability = port => port == 2 ? Available([Adult], Departure(new DateOnly(2026, 11, 17), 16, 40)) : Available([Adult]),
        };

        Assert.True(await Create(catalog).IsOnSaleAsync(3, CancellationToken.None));
    }

    [Fact]
    public async Task ToursWithoutDeparturesOnlyPastDeparturesOrNoTlPriceAreNotOnSale()
    {
        var today = new DateOnly(2026, 10, 26);
        foreach (var availability in new[]
        {
            Available([Adult]),
            // 11:30 in Istanbul has already left at 12:00.
            Available([Adult], Departure(today, 11, 30)),
            Available([Adult with { Currency = "EUR" }], Departure(today, 16, 30)),
            Available([], Departure(today, 16, 30)),
        })
        {
            var catalog = new FakeCatalog { Ports = [new(1, "Üsküdar", 1)], Availability = _ => availability };
            Assert.False(await Create(catalog).IsOnSaleAsync(9, CancellationToken.None));
        }

        var later = new FakeCatalog { Ports = [new(1, "Üsküdar", 1)], Availability = _ => Available([Adult], Departure(today, 12, 30)) };
        Assert.True(await Create(later).IsOnSaleAsync(9, CancellationToken.None));
    }

    [Fact]
    public async Task ResultIsCachedButAProviderOutageNeverHidesTheTour()
    {
        var catalog = new FakeCatalog { Ports = [new(1, "Üsküdar", 1)], Availability = _ => Available([Adult]) };
        var service = Create(catalog);

        Assert.False(await service.IsOnSaleAsync(9, CancellationToken.None));
        Assert.False(await service.IsOnSaleAsync(9, CancellationToken.None));
        Assert.Equal(1, catalog.AvailabilityCalls);

        var failing = new FakeCatalog { Ports = [new(1, "Üsküdar", 1)], Fail = true };
        Assert.True(await Create(failing).IsOnSaleAsync(9, CancellationToken.None));
    }

    private static TourSaleStatus Create(FakeCatalog catalog) =>
        new(catalog, new MemoryCache(new MemoryCacheOptions()), new FixedClock());

    private static TourAvailability Available(IReadOnlyList<TourPrice> prices, params TourDeparture[] departures) =>
        new(9, "Tur", "Boğaz Turu", 1, prices, departures);

    private static TourDeparture Departure(DateOnly date, int hour, int minute) =>
        new(100 + hour, 100 + hour, 1, "İskele", date, new TimeOnly(hour, minute));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeCatalog : ITourCatalogService
    {
        public IReadOnlyList<TourPort> Ports { get; init; } = [];
        public Func<int, TourAvailability?> Availability { get; init; } = _ => null;
        public bool Fail { get; init; }
        public int AvailabilityCalls { get; private set; }

        public Task<IReadOnlyList<TourCatalogItem>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TourCatalogItem>>([]);

        public Task<IReadOnlyList<TourPort>> ListPortsAsync(int externalTourId, CancellationToken cancellationToken) =>
            Fail ? throw new TourCatalogUnavailableException("down") : Task.FromResult(Ports);

        public Task<TourAvailability?> GetAvailabilityAsync(int externalTourId, int externalDeparturePortId, int saleType, CancellationToken cancellationToken)
        {
            AvailabilityCalls++;
            return Task.FromResult(Availability(externalDeparturePortId));
        }
    }
}
