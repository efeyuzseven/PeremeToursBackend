using PeremeTours.Application.Tours;

namespace PeremeTours.UnitTests;

public sealed class TourBookingServiceTests
{
    private static readonly DateOnly TourDate = new(2026, 10, 3);
    private static readonly TourTicketSelection[] MixedTickets = [new(145, 2), new(146, 1)];

    [Fact]
    public async Task MixedTicketTypesUseFreshApiAmountsAndNeverClientTotals()
    {
        var catalog = new FakeCatalog();
        var service = new TourBookingService(catalog, new FixedClock());
        var result = await service.QuoteAsync(Command(MixedTickets), CancellationToken.None);

        Assert.Equal(4050m, result.Amount);
        Assert.Equal(3, result.GuestCount);
        Assert.Equal("TRY", result.Currency);
        Assert.Equal("Kabataş", result.PortName);
        Assert.Equal("Without Alcohol", result.Tickets[0].TicketTypeEn);
        Assert.Equal(1, catalog.AvailabilityCalls);

        catalog.NonAlcoholAmount = 1200m;
        var updated = await service.QuoteAsync(Command(MixedTickets), CancellationToken.None);
        Assert.Equal(4150m, updated.Amount);
        Assert.Equal(2, catalog.AvailabilityCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(13)]
    public async Task InvalidQuantitiesAreRejectedBeforeUpstreamRequest(int quantity)
    {
        var catalog = new FakeCatalog();
        var service = new TourBookingService(catalog, new FixedClock());
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            Command([new(145, quantity)]), CancellationToken.None));
        Assert.Equal(0, catalog.AvailabilityCalls);
    }

    [Fact]
    public async Task DuplicateTypesAndMoreThanTwelveGuestsAreRejected()
    {
        var service = new TourBookingService(new FakeCatalog(), new FixedClock());
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            Command([new(145, 1), new(145, 1)]), CancellationToken.None));
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            Command([new(145, 8), new(146, 8)]), CancellationToken.None));
    }

    [Fact]
    public async Task WrongPortDateDepartureAndRemovedTypeAreRejected()
    {
        var service = new TourBookingService(new FakeCatalog(), new FixedClock());
        var command = Command(MixedTickets);
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            command with { ExternalDeparturePortId = 9 }, CancellationToken.None));
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            command with { TourDate = TourDate.AddDays(1) }, CancellationToken.None));
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            command with { ExternalDepartureId = 999 }, CancellationToken.None));
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            Command([new(999, 1)]), CancellationToken.None));
    }

    [Fact]
    public async Task DeparturesThatHavePassedInIstanbulAreRejected()
    {
        // 20:30 in Istanbul = 17:30 UTC. At exactly departure time it is too late.
        var service = new TourBookingService(new FakeCatalog(), new FixedClock(
            new DateTimeOffset(2026, 10, 3, 17, 30, 0, TimeSpan.Zero)));
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            Command(MixedTickets), CancellationToken.None));
    }

    [Theory]
    [InlineData("EUR", 10)]
    [InlineData("TRY", -10)]
    public async Task UnsupportedCurrencyAndNegativeAmountsAreRejected(string currency, int amount)
    {
        var service = new TourBookingService(new FakeCatalog { Currency = currency, NonAlcoholAmount = amount }, new FixedClock());
        await Assert.ThrowsAsync<TourBookingValidationException>(() => service.QuoteAsync(
            Command([new(145, 1)]), CancellationToken.None));
    }

    private static TourQuoteCommand Command(IReadOnlyList<TourTicketSelection> tickets) =>
        new(2, 3, 8366, TourDate, tickets);

    private sealed class FixedClock(DateTimeOffset? now = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now ?? new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeCatalog : ITourCatalogService
    {
        public int AvailabilityCalls { get; private set; }
        public decimal NonAlcoholAmount { get; set; } = 1150m;
        public string Currency { get; set; } = "TRY";

        public Task<IReadOnlyList<TourCatalogItem>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TourCatalogItem>>([]);

        public Task<IReadOnlyList<TourPort>> ListPortsAsync(int externalTourId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TourPort>>([new(3, "Kabataş", 1)]);

        public Task<TourAvailability?> GetAvailabilityAsync(int externalTourId, int externalDeparturePortId, int saleType, CancellationToken cancellationToken)
        {
            AvailabilityCalls++;
            return Task.FromResult<TourAvailability?>(new TourAvailability(
                2, "Türk Gecesi", "Türk Gecesi", 1,
                [new(145, 6, "Alkolsüz", NonAlcoholAmount, Currency, 20, "Without Alcohol"), new(146, 5, "Alkollü", 1750, "TRY", 20, "With Alcohol")],
                [new(8366, 0, 3, "Kabataş", TourDate, new TimeOnly(20, 30))]
            ));
        }
    }
}
