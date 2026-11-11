using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tickets;

namespace PeremeTours.UnitTests;

// The admin ticket lists show the customer, who started the refund, why and when.
public sealed class TicketCancellationDetailsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _ticketId = Guid.NewGuid();
    private readonly DateTime _requestedAt = new(2026, 11, 10, 9, 30, 0, DateTimeKind.Utc);

    public TicketCancellationDetailsTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, TicketCancellationTests.SqliteTestModel>().Options);
        _db.Database.EnsureCreated();
        _db.TourTickets.Add(new TourTicket { Id = _ticketId, TicketCode = "PRM-REFUND-INFO", TourName = "Sunset", CustomerName = "Deniz Kaya",
            CustomerEmail = "deniz@example.test", GuestCount = 1, Amount = 350, Currency = "TRY", Status = TicketStatus.Cancelled,
            PaymentStatus = TicketPaymentStatus.Refunded, TicketingStatus = TicketingStatus.Issued, PaymentProvider = "Ziraat",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        _db.TicketCancellations.Add(new TicketCancellation { TicketId = _ticketId, ActorUserId = _actor, Reason = "Müşteri talebi", Amount = 350,
            Status = TicketCancellationStatus.Completed, BankOperation = "Void", BankTransactionId = "MOCK-TX",
            RequestedAtUtc = _requestedAt, CompletedAtUtc = _requestedAt.AddMinutes(1), UpdatedAtUtc = _requestedAt.AddMinutes(1) });
        _db.TicketErrorRecords.Add(new TicketErrorRecord { Id = Guid.NewGuid(), TicketId = _ticketId, Stage = TicketErrorStage.Cancellation,
            Code = "PROVIDER_CANCELLATION_UNKNOWN", Message = "EasyTicket did not answer", CreatedAtUtc = _requestedAt.AddMinutes(1) });
        _db.SaveChanges();
    }

    [Fact]
    public async Task TicketAndErrorListsShowWhoRefundedAndWhy()
    {
        _db.Users.Add(new UserAccount { Id = _actor, Email = "ayse@example.test", NormalizedEmail = "AYSE@EXAMPLE.TEST", FirstName = "Ayşe",
            LastName = "Yılmaz", PasswordHash = "hash", Role = "Admin", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();

        var ticket = (await new TicketService(_db).ListAsync(CancellationToken.None)).Single();
        Assert.Equal("Ayşe Yılmaz", ticket.Cancellation!.ActorName);
        Assert.Equal("ayse@example.test", ticket.Cancellation.ActorEmail);
        Assert.Equal("Müşteri talebi", ticket.Cancellation.Reason);
        Assert.Equal(_requestedAt, ticket.Cancellation.RequestedAtUtc);

        var error = (await new TicketErrorService(_db).ListAsync("deniz", null, 1, 20, CancellationToken.None)).Items.Single();
        Assert.Equal("Deniz Kaya", error.CustomerName);
        Assert.Equal("deniz@example.test", error.CustomerEmail);
        Assert.Equal("Ayşe Yılmaz", error.Cancellation!.ActorName);
        Assert.Equal("Müşteri talebi", error.Cancellation.Reason);
    }

    [Fact]
    public async Task MissingStaffUserStillShowsReasonAndTime()
    {
        var error = (await new TicketErrorService(_db).ListAsync(null, null, 1, 20, CancellationToken.None)).Items.Single();
        Assert.Null(error.Cancellation!.ActorName);
        Assert.Null(error.Cancellation.ActorEmail);
        Assert.Equal("Müşteri talebi", error.Cancellation.Reason);
        Assert.Equal(_requestedAt, error.Cancellation.RequestedAtUtc);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
