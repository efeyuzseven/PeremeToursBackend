using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tickets;

namespace PeremeTours.UnitTests;

public sealed class PaymentEmailTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly FakeSender _sender = new();
    private readonly FixedClock _clock = new();

    public PaymentEmailTests()
    {
        _connection.Open();
        _db = CreateContext();
        _db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData("tr", "Ödemeniz tamamlandı.")]
    [InlineData("en", "Your payment is complete.")]
    public async Task TemplateIsLocalizedEscapesHtmlAndExcludesPrivatePassengerFields(string language, string subject)
    {
        var ticket = Ticket(); ticket.CustomerLanguage = language;
        ticket.CustomerName = "<script>alert('x')</script>";
        ticket.TourName = "Sunset <b>Tour</b>";
        ticket.Passengers.Add(new TourPassenger
        {
            Id = Guid.NewGuid(), Sequence = 0, FirstName = "Private", LastName = "Passenger", Gender = "male",
            Nationality = "TR", IdentityNumber = "12345678901", BirthDate = new DateOnly(1990, 1, 1), Pnr = "DEMO-PNR-NOT-A-TICKET",
        });
        var content = PaymentEmailTemplate.Render(ticket, new MailOptions());
        Assert.Contains(subject, content.Subject, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", content.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", content.Html, StringComparison.Ordinal);
        Assert.Contains("DEMO-PNR-NOT-A-TICKET", content.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", content.Html + content.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Passenger", content.Html + content.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("1990", content.Html + content.Text, StringComparison.Ordinal);
        ticket.CustomerName = "Deneme Misafir";
        ticket.TourName = "Sunset · Gün Batımı Turu";
        var preview = PaymentEmailTemplate.Render(ticket, new MailOptions());
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"payment-email-preview-{language}.html"), preview.Html);
    }

    [Fact]
    public void UncertainTicketIssuanceNeverPretendsTicketsAreReady()
    {
        var ticket = Ticket(); ticket.TicketingStatus = TicketingStatus.ReviewRequired;
        var content = PaymentEmailTemplate.Render(ticket, new MailOptions());
        Assert.Contains("Bilet kesimi", content.Text, StringComparison.Ordinal);
        Assert.Contains("tekrar ödeme yapmayın", content.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Biletleriniz oluşturuldu", content.Text, StringComparison.Ordinal);
        ticket.PaymentStatus = TicketPaymentStatus.Failed;
        Assert.Throws<InvalidOperationException>(() => PaymentEmailTemplate.Render(ticket, new MailOptions()));
    }

    [Fact]
    public void IssuedTicketMailLinksOnlyToTheRealEasyTicketVoucher()
    {
        var ticket = Ticket();
        ticket.ExternalVoucherGuid = "11111111-1111-4111-8111-111111111111";
        var content = PaymentEmailTemplate.Render(ticket, new MailOptions());
        var link = "https://easyticket.denturonline.com/bilet.aspx?guid=11111111-1111-4111-8111-111111111111";
        Assert.Contains(link, content.Html, StringComparison.Ordinal);
        Assert.Contains(link, content.Text, StringComparison.Ordinal);
        ticket.ExternalVoucherGuid = "javascript:alert('x')";
        Assert.DoesNotContain("bilet.aspx", PaymentEmailTemplate.Render(ticket, new MailOptions()).Html, StringComparison.Ordinal);
        ticket.ExternalVoucherGuid = "11111111-1111-4111-8111-111111111111";
        ticket.TicketingStatus = TicketingStatus.ReviewRequired;
        Assert.DoesNotContain("bilet.aspx", PaymentEmailTemplate.Render(ticket, new MailOptions()).Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationClaimPreventsQueuedPaymentConfirmationFromBeingSent()
    {
        await QueueAsync();
        var ticket = await _db.TourTickets.SingleAsync();
        _db.TicketCancellations.Add(new TicketCancellation { TicketId = ticket.Id, ActorUserId = Guid.NewGuid(), Reason = "Test", Amount = ticket.Amount,
            Status = TicketCancellationStatus.Processing, RequestedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(0, _sender.Calls);
    }

    [Fact]
    public async Task SuccessfulSendIsPersistedAndCannotBeSentAgain()
    {
        await QueueAsync();
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(1, _sender.Calls);
        var email = await _db.PaymentEmails.SingleAsync();
        Assert.Equal(PaymentEmailStatus.Sent, email.Status);
        Assert.NotNull(email.SentAtUtc);
        Assert.Null(email.LockToken);
        Assert.Empty(await _db.TicketErrorRecords.ToListAsync());
    }

    [Fact]
    public async Task EmailFailureNeverChangesThePaidOrderAndAppearsInAdminErrors()
    {
        await QueueAsync();
        _sender.Failure = new EmailDeliveryException("SMTP_TLS_FAILED", "SMTP sertifikası kontrol edilmeli.");
        await Processor().ProcessNextAsync(CancellationToken.None);
        Assert.Equal(TicketPaymentStatus.Paid, (await _db.TourTickets.SingleAsync()).PaymentStatus);
        Assert.Equal(TicketingStatus.Issued, (await _db.TourTickets.SingleAsync()).TicketingStatus);
        Assert.Equal(PaymentEmailStatus.Failed, (await _db.PaymentEmails.SingleAsync()).Status);
        var page = await new TicketErrorService(_db).ListAsync("demo", TicketErrorStage.Email, 1, 20, CancellationToken.None);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("SMTP_TLS_FAILED", Assert.Single(page.Items).Code);
        Assert.Equal(PaymentEmailStatus.Failed, page.Items[0].EmailStatus);
        Assert.Empty((await new TicketErrorService(_db).ListAsync(null, TicketErrorStage.Payment, 1, 20, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task TransientFailuresRetryOnlyEmailAndStopAtTheAttemptLimit()
    {
        await QueueAsync();
        _sender.Failure = new EmailDeliveryException("SMTP_CONNECT_FAILED", "Bağlantı kurulamadı.", canRetry: true);
        for (var index = 0; index < 4; index++)
        {
            _db.ChangeTracker.Clear();
            await Processor().ProcessNextAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }
        _db.ChangeTracker.Clear();
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(4, _sender.Calls);
        Assert.Equal(PaymentEmailStatus.Failed, (await _db.PaymentEmails.SingleAsync()).Status);
        Assert.Equal(4, await _db.TicketErrorRecords.CountAsync());
        Assert.Equal(TicketPaymentStatus.Paid, (await _db.TourTickets.SingleAsync()).PaymentStatus);
    }

    [Fact]
    public async Task AmbiguousSendIsNotAutomaticallyRetried()
    {
        await QueueAsync();
        _sender.Failure = new EmailDeliveryException("SMTP_RESULT_UNKNOWN", "Gönderim sonucu belirsiz.", isAmbiguous: true);
        await Processor().ProcessNextAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(1, _sender.Calls);
        Assert.Equal(PaymentEmailStatus.ReviewRequired, (await _db.PaymentEmails.SingleAsync()).Status);
    }

    [Fact]
    public async Task ExpiredClaimIsMarkedForReviewWithoutResending()
    {
        await QueueAsync();
        var email = await _db.PaymentEmails.SingleAsync();
        email.Status = PaymentEmailStatus.Processing;
        email.LockedUntilUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        email.LockToken = Guid.NewGuid();
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        await Processor().ProcessNextAsync(CancellationToken.None);
        Assert.Equal(0, _sender.Calls);
        Assert.Equal(PaymentEmailStatus.ReviewRequired, (await _db.PaymentEmails.SingleAsync()).Status);
        Assert.Equal("SMTP_RESULT_UNKNOWN", Assert.Single(await _db.TicketErrorRecords.ToListAsync()).Code);
    }

    [Fact]
    public async Task ConcurrentWorkersCannotSendTheSamePaymentEmail()
    {
        await QueueAsync();
        _sender.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Processor().ProcessNextAsync(CancellationToken.None);
        using var secondDb = CreateContext();
        Assert.False(await new PaymentEmailProcessor(secondDb, _sender, _clock).ProcessNextAsync(CancellationToken.None));
        _sender.Gate.SetResult();
        await first;
        Assert.Equal(1, _sender.Calls);
    }

    [Fact]
    public async Task QueueWaitsForTicketIssuanceButNeverClaimsAnUnpaidOrder()
    {
        await QueueAsync();
        var ticket = await _db.TourTickets.SingleAsync(); ticket.TicketingStatus = TicketingStatus.Processing;
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        await Processor().ProcessNextAsync(CancellationToken.None);
        Assert.Equal(0, _sender.Calls);
        Assert.Equal(0, (await _db.PaymentEmails.SingleAsync()).AttemptCount);
        _clock.Advance(TimeSpan.FromMinutes(6)); _db.ChangeTracker.Clear();
        await Processor().ProcessNextAsync(CancellationToken.None);
        Assert.Equal(1, _sender.Calls);
    }

    [Fact]
    public async Task UnpaidOrdersAreNeverEmailedEvenIfAnInvalidQueueEntryExists()
    {
        await QueueAsync();
        var ticket = await _db.TourTickets.SingleAsync(); ticket.PaymentStatus = TicketPaymentStatus.Failed;
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(0, _sender.Calls);
    }

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("password=sensitive")]
    [InlineData("PAN-4111111111111111")]
    public void ArbitraryProviderDataCannotBecomeAnErrorCode(string value)
    {
        Assert.Equal("UNKNOWN_ERROR", PaymentDiagnostics.SafeCode(value, "UNKNOWN_ERROR"));
        Assert.Null(PaymentDiagnostics.SafeProviderCode(value));
    }

    [Fact]
    public async Task MissingSmtpConfigurationFailsBeforeAnyNetworkRequest()
    {
        var sender = new SmtpPaymentEmailSender(Options.Create(new MailOptions()));
        var failure = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(Ticket(), CancellationToken.None));
        Assert.Equal("SMTP_CONFIG_MISSING", failure.Code);
    }

    private PeremeToursDbContext CreateContext() => new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection).Options);
    private PaymentEmailProcessor Processor() => new(_db, _sender, _clock);
    private async Task QueueAsync()
    {
        var ticket = Ticket();
        _db.TourTickets.Add(ticket);
        _db.PaymentEmails.Add(new PaymentEmail { Ticket = ticket, TicketId = ticket.Id, Status = PaymentEmailStatus.Queued,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime, NextAttemptAtUtc = _clock.GetUtcNow().UtcDateTime });
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
    }
    private static TourTicket Ticket() => new()
    {
        Id = Guid.NewGuid(), TicketCode = "PRM-DEMO-NOT-A-TICKET", CustomerName = "Deneme Misafir", CustomerEmail = "example@example.com",
        TourName = "Sunset", TourDate = new DateOnly(2099, 10, 3), DepartureTime = new TimeOnly(18, 30), DeparturePortName = "Kabataş",
        GuestCount = 1, Amount = 350, Currency = "TRY", PaymentStatus = TicketPaymentStatus.Paid, TicketingStatus = TicketingStatus.Issued,
        Status = TicketStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class FakeSender : IPaymentEmailSender
    {
        public int Calls { get; private set; }
        public EmailDeliveryException? Failure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public async Task SendAsync(TourTicket ticket, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(TicketPaymentStatus.Paid, ticket.PaymentStatus);
            if (Gate is not null) await Gate.Task;
            if (Failure is not null) throw Failure;
        }
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
