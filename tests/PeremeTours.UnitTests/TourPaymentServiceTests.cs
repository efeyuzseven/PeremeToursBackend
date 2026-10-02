using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.UnitTests;

public sealed class TourPaymentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly FakeBooking _booking = new();
    private readonly FakeBank _bank = new();
    private readonly FakeSales _sales = new();
    private readonly TourPaymentService _service;

    public TourPaymentServiceTests()
    {
        _connection.Open();
        _db = new PeremeToursDbContext(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _service = new TourPaymentService(_db, _booking, _bank, _sales,
            Options.Create(ConfiguredOptions()), Options.Create(new EasyTicketOptions { BaseUrl = "https://tickets.example.test", ApiKey = "fake-key" }),
            TimeProvider.System, NullLogger<TourPaymentService>.Instance);
    }

    [Fact]
    public async Task MixedTicketsUseFreshServerAmountsAndStorePassengersButNotCards()
    {
        var result = await _service.StartAsync(Command(), CancellationToken.None);
        var ticket = await _db.TourTickets.Include(item => item.Passengers).SingleAsync();
        Assert.Equal(4050m, result.Amount);
        Assert.Equal(8366, ticket.ExternalTripId); // The real feed supplies id, not sefer_Id.
        Assert.Equal(3, ticket.Passengers.Count);
        Assert.Equal(1150m, ticket.Passengers[0].UnitAmount);
        Assert.Equal(1750m, ticket.Passengers[2].UnitAmount);
        Assert.Equal(TicketPaymentStatus.Pending, ticket.PaymentStatus);
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(0, _bank.AuthCalls);
        Assert.Equal(0, _sales.Calls);
        Assert.DoesNotContain(_db.Model.GetEntityTypes().SelectMany(type => type.GetProperties()),
            property => property.Name.Contains("Card", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("SecurityCode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PriceChangeCannotChargeOrCreateAnOrder()
    {
        await Assert.ThrowsAsync<PaymentConflictException>(() => _service.StartAsync(Command() with { ExpectedAmount = 1 }, CancellationToken.None));
        Assert.Empty(await _db.TourTickets.ToListAsync());
        Assert.Equal(0, _bank.StartCalls);
    }

    [Fact]
    public async Task PassengerCountsMustMatchEachSelectedTicketType()
    {
        var command = Command();
        await Assert.ThrowsAsync<PaymentValidationException>(() => _service.StartAsync(command with
        {
            Passengers = command.Passengers.Select(item => item with { ExternalPriceId = 145 }).ToArray(),
        }, CancellationToken.None));
        Assert.Equal(0, _bank.StartCalls);
    }

    [Theory]
    [InlineData("4111111111111112", "123", 12, 2099)]
    [InlineData("4111111111111111", "a23", 12, 2099)]
    [InlineData("4111111111111111", "123", 0, 2099)]
    [InlineData("4111111111111111", "123", 12, 2020)]
    public async Task InvalidCardIsRejectedBeforeBankOrDatabase(string number, string cvc, int month, int year)
    {
        await Assert.ThrowsAsync<PaymentValidationException>(() => _service.StartAsync(Command() with
        {
            Card = new PaymentCard("Test User", number, cvc, month, year),
        }, CancellationToken.None));
        Assert.Empty(await _db.TourTickets.ToListAsync());
        Assert.Equal(0, _bank.StartCalls);
    }

    [Fact]
    public async Task InvalidIdentityAndPrivacyConsentAreRejected()
    {
        var command = Command();
        await Assert.ThrowsAsync<PaymentValidationException>(() => _service.StartAsync(command with { PrivacyNoticeAccepted = false }, CancellationToken.None));
        await Assert.ThrowsAsync<PaymentValidationException>(() => _service.StartAsync(command with
        {
            Passengers = command.Passengers.Select(item => item with { IdentityNumber = "123" }).ToArray(),
        }, CancellationToken.None));
        Assert.Equal(0, _bank.StartCalls);
    }

    [Fact]
    public async Task SameAttemptCannotInitializeTwice()
    {
        var command = Command();
        await _service.StartAsync(command, CancellationToken.None);
        await Assert.ThrowsAsync<PaymentConflictException>(() => _service.StartAsync(command, CancellationToken.None));
        Assert.Equal(1, _bank.StartCalls);
        Assert.Single(await _db.TourTickets.ToListAsync());
    }

    [Fact]
    public async Task VerifiedPaymentIssuesTicketsAndRepeatedCallbackDoesNotChargeOrIssueAgain()
    {
        var command = Command();
        var start = await _service.StartAsync(command, CancellationToken.None);
        _db.ChangeTracker.Clear();
        var callback = Callback(start);
        var result = await _service.CompleteAsync(callback, CancellationToken.None);
        Assert.True(result.IsSuccessful);
        await _service.CompleteAsync(callback, CancellationToken.None);
        Assert.Equal(1, _bank.AuthCalls);
        Assert.Equal(1, _sales.Calls);
        var status = await _service.GetStatusAsync(command.AttemptId, CancellationToken.None);
        Assert.Equal("Paid", status!.PaymentStatus);
        Assert.Equal("Issued", status.TicketingStatus);
        Assert.Equal(3, status.Tickets.Count);
        Assert.All(status.Tickets, item => Assert.NotEmpty(item.Pnr!));
        Assert.Equal(TicketStatus.Confirmed, (await _db.TourTickets.SingleAsync()).Status);
    }

    [Fact]
    public async Task InvalidHashDoesNotTouchAnOrder()
    {
        var start = await _service.StartAsync(Command(), CancellationToken.None);
        _bank.HashValid = false;
        var result = await _service.CompleteAsync(Callback(start), CancellationToken.None);
        Assert.False(result.IsSuccessful);
        Assert.Equal(0, _bank.AuthCalls);
        Assert.Equal(TicketPaymentStatus.Pending, (await _db.TourTickets.SingleAsync()).PaymentStatus);
    }

    [Theory]
    [InlineData("amount", "1.00")]
    [InlineData("mdStatus", "2")]
    [InlineData("clientid", "wrong-merchant")]
    [InlineData("cavv", "")]
    public async Task TamperedCallbackNeverSendsAuth(string field, string value)
    {
        var start = await _service.StartAsync(Command(), CancellationToken.None);
        var callback = Callback(start); callback[field] = value;
        await _service.CompleteAsync(callback, CancellationToken.None);
        Assert.Equal(0, _bank.AuthCalls);
        Assert.Equal(0, _sales.Calls);
        Assert.Equal("Failed", (await _service.GetStatusAsync((await _db.TourTickets.SingleAsync()).PaymentAttemptId!.Value, CancellationToken.None))!.PaymentStatus);
    }

    [Fact]
    public async Task DeclinedCardDoesNotIssueTickets()
    {
        var start = await _service.StartAsync(Command(), CancellationToken.None);
        _bank.Approved = false; _db.ChangeTracker.Clear();
        Assert.False((await _service.CompleteAsync(Callback(start), CancellationToken.None)).IsSuccessful);
        Assert.Equal(0, _sales.Calls);
        Assert.Equal(TicketPaymentStatus.Failed, (await _db.TourTickets.SingleAsync()).PaymentStatus);
    }

    [Fact]
    public async Task AmbiguousBankResponseNeverRetriesAuth()
    {
        var command = Command(); var start = await _service.StartAsync(command, CancellationToken.None);
        _bank.ThrowOnAuth = true; _db.ChangeTracker.Clear();
        await _service.CompleteAsync(Callback(start), CancellationToken.None);
        await _service.CompleteAsync(Callback(start), CancellationToken.None);
        Assert.Equal(1, _bank.AuthCalls);
        Assert.Equal(0, _sales.Calls);
        Assert.Equal("ReviewRequired", (await _service.GetStatusAsync(command.AttemptId, CancellationToken.None))!.PaymentStatus);
    }

    [Fact]
    public async Task TicketingFailureKeepsPaidButDoesNotConfirmBookingOrRetrySale()
    {
        var command = Command(); var start = await _service.StartAsync(command, CancellationToken.None);
        _sales.Fail = true; _db.ChangeTracker.Clear();
        Assert.True((await _service.CompleteAsync(Callback(start), CancellationToken.None)).IsSuccessful);
        await _service.CompleteAsync(Callback(start), CancellationToken.None);
        var status = await _service.GetStatusAsync(command.AttemptId, CancellationToken.None);
        Assert.Equal("Paid", status!.PaymentStatus);
        Assert.Equal("ReviewRequired", status.TicketingStatus);
        Assert.Empty(status.Tickets);
        Assert.Equal(TicketStatus.Pending, (await _db.TourTickets.SingleAsync()).Status);
        Assert.Equal(1, _bank.AuthCalls); Assert.Equal(1, _sales.Calls);
    }

    [Fact]
    public async Task OverlappingCallbacksOnlyAuthorizeOnce()
    {
        var start = await _service.StartAsync(Command(), CancellationToken.None);
        _db.ChangeTracker.Clear();
        _bank.AuthGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _service.CompleteAsync(Callback(start), CancellationToken.None);
        await _service.CompleteAsync(Callback(start), CancellationToken.None);
        _bank.AuthGate.SetResult();
        await first;
        Assert.Equal(1, _bank.AuthCalls); Assert.Equal(1, _sales.Calls);
    }

    [Fact]
    public void CardAndPassengerRecordStringsAreRedacted()
    {
        Assert.DoesNotContain("411111", Command().Card.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", Command().Passengers[0].ToString(), StringComparison.Ordinal);
    }

    private static ZiraatPosOptions ConfiguredOptions() => new()
    {
        Enabled = true, MerchantId = "fake-merchant", ClientId = "fake-client", StoreKey = "fake-store-key",
        ApiUser = "fake-user", ApiPassword = "fake-password", GatewayUrl = "https://bank.example.test/start",
        ApiUrl = "https://bank.example.test/api", CallbackUrl = "https://api.example.test/callback", FrontendOrigin = "https://example.test",
    };
    private static StartTourPaymentCommand Command() => new(2, 3, 8366, new DateOnly(2099, 10, 3),
        [new(145, 2), new(146, 1)],
        [new(145, "Test", "One", "male", "TR", "12345678901", new DateOnly(1990, 1, 1)),
         new(145, "Test", "Two", "female", "TR", "12345678901", new DateOnly(1990, 1, 1)),
         new(146, "Test", "Three", "female", "foreign", "AA123456", new DateOnly(1990, 1, 1))],
        4050m, Guid.NewGuid(), true, "Test User", "test@example.com", "+905551234567", "tr",
        new("Test User", "4111111111111111", "123", 12, DateTime.UtcNow.Year + 1), null);

    private static Dictionary<string, string> Callback(StartTourPaymentResult start) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["oid"] = start.TicketCode, ["amount"] = "4050.00", ["hashAlgorithm"] = "ver3", ["clientid"] = "fake-merchant",
        ["currency"] = "949", ["mdStatus"] = "1", ["md"] = "fake-md", ["xid"] = "fake-xid", ["eci"] = "05", ["cavv"] = "fake-cavv",
    };

    private sealed class FakeBooking : ITourBookingService
    {
        public Task<TourQuote> QuoteAsync(TourQuoteCommand command, CancellationToken cancellationToken) => Task.FromResult(new TourQuote(
            2, "Turkish Night", 3, "Kabataş", 8366, command.TourDate, new TimeOnly(20, 30),
            [new(145, "Alkolsüz", null, 2, 1150m, 2300m), new(146, "Alkollü", null, 1, 1750m, 1750m)], 3, 4050m, "TRY", DateTimeOffset.UtcNow));
    }
    private sealed class FakeBank : IZiraatPosGateway
    {
        public int StartCalls { get; private set; }
        public int AuthCalls { get; private set; }
        public bool HashValid { get; set; } = true;
        public bool Approved { get; set; } = true;
        public bool ThrowOnAuth { get; set; }
        public TaskCompletionSource? AuthGate { get; set; }
        public Task<string> StartThreeDSecureAsync(ZiraatPaymentRequest payment, CancellationToken cancellationToken)
        {
            StartCalls++; return Task.FromResult("<html>mock bank</html>");
        }
        public bool VerifyCallback(IReadOnlyDictionary<string, string> formFields) => HashValid;
        public async Task<ZiraatFinalizationResult> FinalizeAsync(string orderId, decimal amount, IReadOnlyDictionary<string, string> formFields, CancellationToken cancellationToken)
        {
            AuthCalls++;
            if (AuthGate is not null) await AuthGate.Task;
            if (ThrowOnAuth) throw new PaymentGatewayException("mock timeout");
            return new(Approved, "fake-auth", "fake-host", Approved ? "00" : "05", null);
        }
    }
    private sealed class FakeSales : IEasyTicketSalesGateway
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<EasyTicketSaleResult> IssueAsync(TourTicket ticket, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) throw new PaymentTicketingException();
            Assert.Equal(TicketPaymentStatus.Paid, ticket.PaymentStatus);
            return Task.FromResult(new EasyTicketSaleResult(true, "mock-voucher",
                ticket.Passengers.Select((_, i) => new EasyTicketIssuedTicket($"mock-ticket-{i}", $"mock-pnr-{i}")).ToArray()));
        }
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
