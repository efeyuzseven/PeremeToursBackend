using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tickets;

namespace PeremeTours.UnitTests;

public sealed class TicketCancellationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly FakeBank _bank = new();
    private readonly FakeProvider _provider = new();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TourTicket _ticket;
    private readonly List<string> _events = [];
    public TicketCancellationTests()
    {
        _connection.Open(); _db = Context(); _db.Database.EnsureCreated();
        _bank.Events = _events; _provider.Events = _events;
        _ticket = new TourTicket { Id = Guid.NewGuid(), TicketCode = "PRM-MOCK-CANCEL", TourName = "Sunset", CustomerName = "Test",
            CustomerEmail = "test@example.test", GuestCount = 1, Amount = 350, Currency = "TRY", Status = TicketStatus.Confirmed,
            PaymentStatus = TicketPaymentStatus.Paid, TicketingStatus = TicketingStatus.Issued, PaymentProvider = "Ziraat",
            ExternalVoucherGuid = "11111111-1111-4111-8111-111111111111", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        _ticket.Passengers.Add(new TourPassenger { Id = Guid.NewGuid(), Sequence = 0, FirstName = "Demo", LastName = "Guest", Gender = "male",
            Nationality = "TR", IdentityNumber = "12345678901", ExternalTicketGuid = "22222222-2222-4222-8222-222222222222", Pnr = "MOCKPNR" });
        _db.TourTickets.Add(_ticket); _db.SaveChanges(); _db.ChangeTracker.Clear();
    }

    [Theory]
    [InlineData("Void")]
    [InlineData("Credit")]
    public async Task ConfirmedProviderAndBankResultsAreRequiredForFullCancellation(string operation)
    {
        _bank.Operation = operation;
        var result = await Service(_db).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None);
        Assert.Equal("Completed", result!.Status);
        Assert.Equal(operation, result.BankOperation);
        Assert.Equal("check,provider,reverse", string.Join(',', _events));
        var ticket = await _db.TourTickets.SingleAsync();
        Assert.Equal(TicketStatus.Cancelled, ticket.Status);
        Assert.Equal(TicketPaymentStatus.Refunded, ticket.PaymentStatus);
        Assert.Equal(350, (await _db.TicketCancellations.SingleAsync()).Amount);
        Assert.Equal(_actor, (await _db.TicketCancellations.SingleAsync()).ActorUserId);
    }

    [Fact]
    public async Task DuplicateCallsNeverCancelOrRefundTwice()
    {
        await Service(_db).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None);
        using var second = Context();
        Assert.Equal("Completed", (await Service(second).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None))!.Status);
        Assert.Equal(1, _bank.Reversals); Assert.Equal(1, _provider.Calls); Assert.Equal(1, _bank.Checks);
    }

    [Fact]
    public async Task ConcurrentClickReadsTheDurableClaimWithoutRepeatingExternalCalls()
    {
        _bank.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Service(_db).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None);
        using var second = Context();
        Assert.Equal("Processing", (await Service(second).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None))!.Status);
        _bank.Gate.SetResult(); await first;
        Assert.Equal(1, _bank.Checks); Assert.Equal(1, _bank.Reversals); Assert.Equal(1, _provider.Calls);
    }

    [Theory]
    [InlineData("check", "BANK_CANCELLATION_CHECK_FAILED", 0, 0, TicketStatus.Confirmed)]
    [InlineData("provider", "PROVIDER_CANCELLATION_UNKNOWN", 1, 0, TicketStatus.Confirmed)]
    [InlineData("reverse", "BANK_REVERSAL_UNKNOWN", 1, 1, TicketStatus.Cancelled)]
    public async Task AmbiguityIsLoggedWithoutPretendingMoneyWasRefundedOrReplaying(string stage, string code,
        int providerCalls, int reverseCalls, TicketStatus expectedStatus)
    {
        _bank.FailCheck = stage == "check"; _provider.Fail = stage == "provider"; _bank.FailReverse = stage == "reverse";
        var result = await Service(_db).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None);
        Assert.Equal("ReviewRequired", result!.Status); Assert.Equal(code, result.FailureCode);
        using var second = Context();
        await Service(second).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None);
        Assert.Equal(providerCalls, _provider.Calls); Assert.Equal(reverseCalls, _bank.Reversals);
        Assert.Equal(TicketPaymentStatus.Paid, (await _db.TourTickets.SingleAsync()).PaymentStatus);
        Assert.Equal(expectedStatus, (await _db.TourTickets.SingleAsync()).Status);
        Assert.Equal(TicketErrorStage.Cancellation, (await _db.TicketErrorRecords.SingleAsync()).Stage);
    }

    [Fact]
    public async Task BankAlreadyVoidedDoesNotIssueAnotherReversal()
    {
        _bank.AlreadyReversed = true;
        Assert.Equal("Completed", (await Service(_db).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None))!.Status);
        Assert.Equal(0, _bank.Reversals); Assert.Equal(1, _provider.Calls);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("code")]
    [InlineData("used")]
    [InlineData("unpaid")]
    [InlineData("unissued")]
    [InlineData("voucher")]
    public async Task InvalidRequestsFailBeforeAnyExternalOperation(string fault)
    {
        var ticket = await _db.TourTickets.SingleAsync();
        if (fault == "used") ticket.Status = TicketStatus.Used;
        if (fault == "unpaid") ticket.PaymentStatus = TicketPaymentStatus.Pending;
        if (fault == "unissued") ticket.TicketingStatus = TicketingStatus.ReviewRequired;
        if (fault == "voucher") ticket.ExternalVoucherGuid = null;
        await _db.SaveChangesAsync();
        var command = Command() with { ExpectedAmount = fault == "amount" ? 1 : 350, TicketCode = fault == "code" ? "WRONG" : _ticket.TicketCode };
        await Assert.ThrowsAsync<TicketCancellationValidationException>(() => Service(_db).CancelAsync(_ticket.Id, _actor, command, CancellationToken.None));
        Assert.Empty(_events); Assert.Empty(await _db.TicketCancellations.ToListAsync());
    }

    [Fact]
    public async Task ClaimedTicketCannotBeMarkedUsed()
    {
        _bank.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Service(_db).CancelAsync(_ticket.Id, _actor, Command(), CancellationToken.None);
        using var second = Context();
        await Assert.ThrowsAsync<TicketUpdateValidationException>(() => new TicketService(second).UpdateAsync(_ticket.Id,
            new UpdateTicketCommand(null, null, null, null, null, null, null, TicketStatus.Used), CancellationToken.None));
        _bank.Gate.SetResult(); await first;
    }

    [Fact]
    public async Task StaleClaimIsShownForReviewWithoutCallingGateways()
    {
        _db.TicketCancellations.Add(new TicketCancellation { TicketId = _ticket.Id, ActorUserId = _actor, Reason = "Test", Amount = 350,
            Status = TicketCancellationStatus.BankReversalStarted, RequestedAtUtc = DateTime.UtcNow.AddMinutes(-10), UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10) });
        await _db.SaveChangesAsync();
        Assert.Equal("ReviewRequired", (await Service(_db).GetAsync(_ticket.Id, CancellationToken.None))!.Status);
        Assert.Empty(_events);
    }

    private CancelTicketCommand Command() => new(_ticket.TicketCode, 350, "Test cancellation");
    private PeremeToursDbContext Context() => new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection).Options);
    private TicketCancellationService Service(PeremeToursDbContext db) => new(db, _bank, _provider, TimeProvider.System);
    private sealed class FakeBank : IBankCancellationGateway
    {
        public List<string> Events { get; set; } = [];
        public string Operation { get; set; } = "Void";
        public bool AlreadyReversed { get; set; }
        public bool FailCheck { get; set; }
        public bool FailReverse { get; set; }
        public int Checks { get; private set; }
        public int Reversals { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public async Task<BankCancellationCheck> CheckAsync(TourTicket ticket, CancellationToken cancellationToken)
        { Checks++; Events.Add("check"); if (Gate is not null) await Gate.Task;
            if (FailCheck) throw new CancellationGatewayException("BANK_CANCELLATION_CHECK_FAILED"); return new("MOCK-TX", Operation, AlreadyReversed); }
        public Task<string> ReverseAsync(TourTicket ticket, BankCancellationCheck check, CancellationToken cancellationToken)
        { Reversals++; Events.Add("reverse"); if (FailReverse) throw new CancellationGatewayException("BANK_REVERSAL_UNKNOWN"); return Task.FromResult("MOCK-REVERSAL"); }
    }
    private sealed class FakeProvider : IEasyTicketCancellationGateway
    {
        public List<string> Events { get; set; } = [];
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task CancelAsync(TourTicket ticket, CancellationToken cancellationToken)
        { Calls++; Events.Add("provider"); if (Fail) throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNKNOWN"); return Task.CompletedTask; }
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
