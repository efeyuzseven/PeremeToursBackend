using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.UnitTests;

public sealed class ReservationNotificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly FakeSender _sender = new();
    private readonly FixedClock _clock = new();

    public ReservationNotificationTests()
    {
        _connection.Open();
        _db = Context();
        _db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(TourCategoryKeys.Sunset, 2)]
    [InlineData(TourCategoryKeys.Daytime, 2)]
    [InlineData(TourCategoryKeys.TurkishNight, 1)]
    [InlineData(TourCategoryKeys.Bosphorus, 1)]
    [InlineData(null, 1)]
    [InlineData("unknown", 1)]
    public void RoutingUsesVerifiedCategoryAndNotEditableTourName(string? category, int count)
    {
        var ticket = Ticket(); ticket.TourCategoryKey = category; ticket.TourName = "Sunset Daytime Türk Gecesi";
        var notifications = ReservationNotificationRouting.Create(ticket, new MailOptions(), _clock.GetUtcNow());
        Assert.Equal(count, notifications.Count);
        Assert.Contains(notifications, item => item.RecipientEmail == "reservation@pereme.com.tr");
        Assert.Equal(count == 2, notifications.Any(item => item.RecipientEmail == "omacit@pereme.com.tr"));
        Assert.All(notifications, item => Assert.Equal(ticket.Id, item.TicketId));
    }

    [Fact]
    public void DuplicateConfiguredRecipientsAreNormalizedAndQueuedOnce()
    {
        var settings = new MailOptions { ReservationRecipient = " Reservation@Pereme.com.tr ", SunsetDaytimeRecipient = "reservation@pereme.com.tr" };
        var entry = Assert.Single(ReservationNotificationRouting.Create(Ticket(), settings, _clock.GetUtcNow()));
        Assert.Equal("reservation@pereme.com.tr", entry.RecipientEmail);
    }

    [Fact]
    public void OnlyPaidIssuedConfirmedOrdersCreateNotifications()
    {
        var ticket = Ticket(); ticket.PaymentStatus = TicketPaymentStatus.Failed;
        Assert.Empty(ReservationNotificationRouting.Create(ticket, new MailOptions(), _clock.GetUtcNow()));
        ticket.PaymentStatus = TicketPaymentStatus.Paid;
        ticket.TicketingStatus = TicketingStatus.ReviewRequired;
        Assert.Empty(ReservationNotificationRouting.Create(ticket, new MailOptions(), _clock.GetUtcNow()));
        ticket.TicketingStatus = TicketingStatus.Issued;
        ticket.Status = TicketStatus.Cancelled;
        Assert.Empty(ReservationNotificationRouting.Create(ticket, new MailOptions(), _clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => ReservationNotificationTemplate.Render(ticket, new MailOptions()));
    }

    [Fact]
    public void StaffTemplateContainsBookingContactAndTicketSummaryButNotSensitivePassengerFields()
    {
        var ticket = Ticket(); ticket.CustomerName = "<script>alert('x')</script>";
        ticket.CustomerPhone = "+905550000001";
        ticket.Passengers.Add(new TourPassenger { Id = Guid.NewGuid(), Sequence = 0, FirstName = "Private", LastName = "Passenger",
            Gender = "female", Nationality = "TR", IdentityNumber = "12345678901", BirthDate = new DateOnly(1982, 1, 2),
            TicketType = "İkramlı <b>VIP</b>", UnitAmount = 350, Pnr = "MOCK-PNR", ExternalTicketGuid = "22222222-2222-4222-8222-222222222222" });
        var message = ReservationNotificationTemplate.Render(ticket, new MailOptions());
        Assert.Contains("Yeni rezervasyon", message.Subject, StringComparison.Ordinal);
        Assert.Contains(ticket.CustomerEmail, message.Text, StringComparison.Ordinal);
        Assert.Contains(ticket.CustomerPhone, message.Text, StringComparison.Ordinal);
        Assert.Contains("MOCK-PNR", message.Text, StringComparison.Ordinal);
        Assert.Contains("İkramlı", message.Text, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", message.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;VIP&lt;/b&gt;", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", message.Html, StringComparison.Ordinal);
        var bodies = message.Html + message.Text;
        Assert.DoesNotContain("12345678901", bodies, StringComparison.Ordinal);
        Assert.DoesNotContain("1982", bodies, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Passenger", bodies, StringComparison.Ordinal);
        Assert.DoesNotContain("22222222-2222-4222-8222-222222222222", bodies, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomerAndEachStaffRecipientGetSeparateMessagesWithoutCcOrBcc()
    {
        var settings = new MailOptions { Username = "mock", Password = "mock-password", SenderEmail = "sender@example.test" };
        var smtp = new SmtpPaymentEmailSender(Options.Create(settings));
        var ticket = Ticket();
        var customer = smtp.CreateCustomerMessage(ticket);
        Assert.Equal(ticket.CustomerEmail, Assert.IsType<MailboxAddress>(Assert.Single(customer.To)).Address);
        Assert.Empty(customer.Cc); Assert.Empty(customer.Bcc);
        var ids = new HashSet<string>(StringComparer.Ordinal) { Assert.IsType<string>(customer.MessageId) };
        foreach (var notification in ReservationNotificationRouting.Create(ticket, settings, _clock.GetUtcNow()))
        {
            notification.Ticket = ticket;
            var staff = smtp.CreateReservationMessage(notification);
            Assert.Equal(notification.RecipientEmail, Assert.IsType<MailboxAddress>(Assert.Single(staff.To)).Address);
            Assert.Empty(staff.Cc); Assert.Empty(staff.Bcc);
            Assert.DoesNotContain(ticket.CustomerEmail, staff.To.ToString(), StringComparison.Ordinal);
            Assert.True(ids.Add(Assert.IsType<string>(staff.MessageId)));
            Assert.Equal(staff.MessageId, smtp.CreateReservationMessage(notification).MessageId);
        }
        Assert.Equal(3, ids.Count);
        Assert.DoesNotContain("reservation@pereme.com.tr", customer.To.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothRecipientsSendOnceAndCustomerReceiptStateIsUnchanged()
    {
        await QueueAsync();
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(2, _sender.Recipients.Count);
        Assert.Contains("reservation@pereme.com.tr", _sender.Recipients);
        Assert.Contains("omacit@pereme.com.tr", _sender.Recipients);
        _db.ChangeTracker.Clear();
        Assert.All(await _db.ReservationNotifications.ToListAsync(), item => { Assert.Equal(ReservationNotificationStatus.Sent, item.Status); Assert.NotNull(item.SentAtUtc); Assert.Null(item.LockToken); });
        Assert.Equal(PaymentEmailStatus.Sent, (await _db.PaymentEmails.SingleAsync()).Status);
        Assert.Empty(await _db.TicketErrorRecords.ToListAsync());
    }

    [Fact]
    public async Task FailedRecipientCannotBlockOtherRecipientCustomerOrPaidBooking()
    {
        await QueueAsync(); _sender.FailingRecipient = "reservation@pereme.com.tr";
        _sender.Failure = new EmailDeliveryException("SMTP_SEND_REJECTED", "Alıcı reddedildi.");
        await Processor().ProcessNextAsync(CancellationToken.None);
        await Processor().ProcessNextAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
        Assert.Equal(ReservationNotificationStatus.Failed, (await _db.ReservationNotifications.SingleAsync(item => item.RecipientEmail == "reservation@pereme.com.tr")).Status);
        Assert.Equal(ReservationNotificationStatus.Sent, (await _db.ReservationNotifications.SingleAsync(item => item.RecipientEmail == "omacit@pereme.com.tr")).Status);
        Assert.Equal(PaymentEmailStatus.Sent, (await _db.PaymentEmails.SingleAsync()).Status);
        Assert.Equal(TicketPaymentStatus.Paid, (await _db.TourTickets.SingleAsync()).PaymentStatus);
        Assert.Equal(TicketStatus.Confirmed, (await _db.TourTickets.SingleAsync()).Status);
        Assert.Equal("RESERVATION_SMTP_SEND_REJECTED", Assert.Single(await _db.TicketErrorRecords.ToListAsync()).Code);
    }

    [Fact]
    public async Task KnownTransientFailuresRetryOnlyFailingRecipientAndStopAtLimit()
    {
        await QueueAsync(); _sender.FailingRecipient = "reservation@pereme.com.tr";
        _sender.Failure = new EmailDeliveryException("SMTP_CONNECT_FAILED", "Bağlantı yok.", canRetry: true);
        for (var index = 0; index < 4; index++)
        {
            while (await Processor().ProcessNextAsync(CancellationToken.None)) _db.ChangeTracker.Clear();
            _clock.Advance(TimeSpan.FromMinutes(5));
        }
        Assert.Equal(4, _sender.Recipients.Count(item => item == "reservation@pereme.com.tr"));
        Assert.Single(_sender.Recipients, item => item == "omacit@pereme.com.tr");
        Assert.Equal(ReservationNotificationStatus.Failed, (await _db.ReservationNotifications.SingleAsync(item => item.RecipientEmail == "reservation@pereme.com.tr")).Status);
    }

    [Fact]
    public async Task AmbiguousSendIsNotAutomaticallyRepeatedAndOtherRecipientStillSends()
    {
        await QueueAsync(); _sender.FailingRecipient = "reservation@pereme.com.tr";
        _sender.Failure = new EmailDeliveryException("SMTP_RESULT_UNKNOWN", "Belirsiz.", isAmbiguous: true);
        await Processor().ProcessNextAsync(CancellationToken.None);
        await Processor().ProcessNextAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(1)); _db.ChangeTracker.Clear();
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(2, _sender.Recipients.Count);
        Assert.Equal(ReservationNotificationStatus.ReviewRequired, (await _db.ReservationNotifications.SingleAsync(item => item.RecipientEmail == "reservation@pereme.com.tr")).Status);
    }

    [Fact]
    public async Task StaleWorkerClaimIsMarkedForReviewWithoutResending()
    {
        await QueueAsync(TourCategoryKeys.Bosphorus);
        var notification = await _db.ReservationNotifications.SingleAsync();
        notification.Status = ReservationNotificationStatus.Processing;
        notification.LockToken = Guid.NewGuid(); notification.LockedUntilUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Empty(_sender.Recipients);
        _db.ChangeTracker.Clear();
        Assert.Equal(ReservationNotificationStatus.ReviewRequired, (await _db.ReservationNotifications.SingleAsync()).Status);
        Assert.Equal("RESERVATION_SMTP_RESULT_UNKNOWN", Assert.Single(await _db.TicketErrorRecords.ToListAsync()).Code);
    }

    [Fact]
    public async Task ConcurrentWorkersCannotSendOneRecipientTwice()
    {
        await QueueAsync(TourCategoryKeys.Bosphorus);
        _sender.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Processor().ProcessNextAsync(CancellationToken.None);
        using var secondDb = Context();
        Assert.False(await new ReservationNotificationProcessor(secondDb, _sender, _clock).ProcessNextAsync(CancellationToken.None));
        _sender.Gate.SetResult(); await first;
        Assert.Single(_sender.Recipients);
    }

    [Fact]
    public async Task CancellationSkipsUnsentStaffNotifications()
    {
        await QueueAsync();
        var ticket = await _db.TourTickets.SingleAsync();
        _db.TicketCancellations.Add(new TicketCancellation { TicketId = ticket.Id, ActorUserId = Guid.NewGuid(), Reason = "Test",
            Amount = ticket.Amount, Status = TicketCancellationStatus.Processing, RequestedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Empty(_sender.Recipients);
        _db.ChangeTracker.Clear();
        Assert.All(await _db.ReservationNotifications.ToListAsync(), item => Assert.Equal(ReservationNotificationStatus.Skipped, item.Status));
    }

    [Theory]
    [InlineData(TicketPaymentStatus.Failed, TicketingStatus.Issued)]
    [InlineData(TicketPaymentStatus.Paid, TicketingStatus.ReviewRequired)]
    [InlineData(TicketPaymentStatus.Processing, TicketingStatus.Pending)]
    public async Task InvalidQueueEntryCannotNotifyAnUnpaidOrUnissuedBooking(TicketPaymentStatus payment, TicketingStatus ticketing)
    {
        await QueueAsync(); var ticket = await _db.TourTickets.SingleAsync(); ticket.PaymentStatus = payment; ticket.TicketingStatus = ticketing;
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Empty(_sender.Recipients);
        Assert.All(await _db.ReservationNotifications.ToListAsync(), item => Assert.Equal(0, item.AttemptCount));
    }

    [Fact]
    public async Task ExistingPaidBookingsAreNotBackfilledByWorker()
    {
        _db.TourTickets.Add(Ticket()); await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Empty(await _db.ReservationNotifications.ToListAsync());
        Assert.Empty(_sender.Recipients);
    }

    private async Task QueueAsync(string category = TourCategoryKeys.Sunset)
    {
        var ticket = Ticket(); ticket.TourCategoryKey = category;
        _db.TourTickets.Add(ticket);
        _db.PaymentEmails.Add(new PaymentEmail { TicketId = ticket.Id, Status = PaymentEmailStatus.Sent,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime, SentAtUtc = _clock.GetUtcNow().UtcDateTime, NextAttemptAtUtc = _clock.GetUtcNow().UtcDateTime });
        _db.ReservationNotifications.AddRange(ReservationNotificationRouting.Create(ticket, new MailOptions(), _clock.GetUtcNow()));
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
    }

    private static TourTicket Ticket() => new()
    {
        Id = Guid.NewGuid(), TicketCode = "PRM-MOCK-RESERVATION", TourCategoryKey = TourCategoryKeys.Sunset,
        TourName = "Sunset", CustomerName = "Demo Guest", CustomerEmail = "guest@example.test",
        TourDate = new DateOnly(2099, 10, 8), DepartureTime = new TimeOnly(18, 30), DeparturePortName = "Kabataş",
        GuestCount = 1, Amount = 350, Currency = "TRY", PaymentStatus = TicketPaymentStatus.Paid,
        TicketingStatus = TicketingStatus.Issued, Status = TicketStatus.Confirmed,
        CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
    private PeremeToursDbContext Context() => new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection).Options);
    private ReservationNotificationProcessor Processor() => new(_db, _sender, _clock);
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }
    private sealed class FakeSender : IReservationNotificationSender
    {
        public List<string> Recipients { get; } = [];
        public string? FailingRecipient { get; set; }
        public EmailDeliveryException? Failure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public async Task SendAsync(ReservationNotification notification, CancellationToken cancellationToken)
        {
            Recipients.Add(notification.RecipientEmail);
            Assert.Equal(TicketPaymentStatus.Paid, notification.Ticket.PaymentStatus);
            if (Gate is not null) await Gate.Task;
            if (notification.RecipientEmail == FailingRecipient && Failure is not null) throw Failure;
        }
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
