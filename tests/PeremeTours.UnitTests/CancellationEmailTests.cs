using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tickets;

namespace PeremeTours.UnitTests;

public sealed class CancellationEmailTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly Sender _sender = new();
    private readonly Clock _clock = new();
    public CancellationEmailTests()
    {
        _connection.Open(); _db = Context(); _db.Database.EnsureCreated();
    }
    private PeremeToursDbContext Context() => new(new DbContextOptionsBuilder<PeremeToursDbContext>()
        .UseSqlite(_connection).ReplaceService<IModelCustomizer, TestModel>().Options);
    private CancellationEmailProcessor Processor(PeremeToursDbContext? context = null) => new(context ?? _db, _sender, _clock);

    private async Task<CancellationEmail> Queue(string operation = "Void", string? invalid = null, bool createMail = true)
    {
        var ticket = new TourTicket { Id = Guid.NewGuid(), TicketCode = "PRM-MOCK-CANCEL-MAIL", TourName = "Sunset",
            TourDate = new DateOnly(2099, 10, 13), DepartureTime = new TimeOnly(18, 30), DeparturePortName = "Kabataş",
            CustomerName = "Fixture", CustomerEmail = "booking-contact@example.test", Currency = "TRY", Amount = 350,
            GuestCount = 1, Status = TicketStatus.Cancelled, PaymentStatus = TicketPaymentStatus.Refunded,
            PaymentProvider = "Ziraat", TicketingStatus = TicketingStatus.Issued, CreatedAtUtc = _clock.GetUtcNow(), UpdatedAtUtc = _clock.GetUtcNow() };
        var cancellation = new TicketCancellation { TicketId = ticket.Id, Ticket = ticket, ActorUserId = Guid.NewGuid(),
            Reason = "Fixture cancellation", Amount = ticket.Amount, Status = TicketCancellationStatus.Completed,
            BankOperation = operation, BankTransactionId = "MOCK-BANK-TX", BankReversalTransactionId = "MOCK-BANK-REVERSAL",
            RequestedAtUtc = _clock.GetUtcNow().UtcDateTime, UpdatedAtUtc = _clock.GetUtcNow().UtcDateTime,
            CompletedAtUtc = _clock.GetUtcNow().UtcDateTime, ProviderStatus = ProviderCancellationStatus.ReviewRequired };
        ticket.Cancellation = cancellation;
        switch (invalid)
        {
            case "ticket": ticket.Status = TicketStatus.Confirmed; break;
            case "payment": ticket.PaymentStatus = TicketPaymentStatus.Paid; break;
            case "review": cancellation.Status = TicketCancellationStatus.ReviewRequired; break;
            case "date": cancellation.CompletedAtUtc = null; break;
            case "amount": cancellation.Amount = 100; break;
            case "operation": cancellation.BankOperation = "QUERY"; break;
            case "reference": cancellation.BankTransactionId = null; break;
            case "provider": ticket.PaymentProvider = "Other"; break;
            case "currency": ticket.Currency = "USD"; break;
            case "failure": cancellation.FailureCode = "BANK_REVERSAL_UNKNOWN"; break;
        }
        _db.TourTickets.Add(ticket);
        var email = new CancellationEmail { Ticket = ticket, TicketId = ticket.Id, Status = PaymentEmailStatus.Queued,
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime, NextAttemptAtUtc = _clock.GetUtcNow().UtcDateTime };
        if (createMail) _db.CancellationEmails.Add(email);
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear(); return email;
    }

    [Theory]
    [InlineData("Void")]
    [InlineData("Credit")]
    public async Task ApprovedBankReversalSendsOnceEvenIfProviderCancellationNeedsReview(string operation)
    {
        await Queue(operation);
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(1, _sender.Calls);
        var email = await _db.CancellationEmails.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentEmailStatus.Sent, email.Status); Assert.NotNull(email.SentAtUtc);
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(1, _sender.Calls);
        Assert.Equal(TicketCancellationStatus.Completed, (await _db.TicketCancellations.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("payment")]
    [InlineData("review")]
    [InlineData("date")]
    [InlineData("amount")]
    [InlineData("operation")]
    [InlineData("reference")]
    [InlineData("provider")]
    [InlineData("currency")]
    [InlineData("failure")]
    public async Task UnverifiedFinancialStateNeverSendsCancellationMail(string invalid)
    {
        await Queue(invalid: invalid);
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(0, _sender.Calls);
        var email = await _db.CancellationEmails.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentEmailStatus.Failed, email.Status); Assert.Equal("CANCELLATION_EMAIL_NOT_CONFIRMED", email.LastFailureCode);
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("tr", "Void", "İptal / İade Bildirimi", "ödemenizin iptalini")]
    [InlineData("en", "Void", "Cancellation / Refund", "cancellation of your payment")]
    [InlineData("tr", "Credit", "İptal / İade Bildirimi", "ödemenizin iade işlemini")]
    [InlineData("en", "Credit", "Cancellation / Refund", "refund of your payment")]
    public async Task ReceiptIsLocalizedEscapesHtmlAndHasNoQrOrPassengerSecrets(string language, string operation, string subject, string message)
    {
        var email = await Queue(operation); email.Ticket.CustomerLanguage = language;
        email.Ticket.CustomerName = "<script>fixture</script>"; email.Ticket.TourName = "Sunset <b>Tour</b>";
        email.Ticket.CustomerPhone = "PRIVATE-PHONE"; email.Ticket.BankAuthCode = "PRIVATE-AUTH";
        email.Ticket.Passengers.Add(new TourPassenger { Id = Guid.NewGuid(), FirstName = "PRIVATE-FIRST", LastName = "PRIVATE-LAST",
            Gender = "male", Nationality = "TR", IdentityNumber = "12345678901", BirthDate = new DateOnly(1990, 1, 1),
            Pnr = "PRIVATE-PNR", ExternalTicketGuid = "11111111-1111-4111-8111-111111111111" });
        email.Ticket.ExternalVoucherGuid = "PRIVATE-VOUCHER";
        var rendered = CancellationEmailTemplate.Render(email.Ticket);
        Assert.Contains(subject, rendered.Subject, StringComparison.Ordinal); Assert.Contains(message, rendered.Text, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", rendered.Html, StringComparison.Ordinal); Assert.DoesNotContain("<script>", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("350", rendered.Text, StringComparison.Ordinal); Assert.Contains("pereme.com.tr", rendered.Html, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "PRIVATE-PHONE", "PRIVATE-AUTH", "PRIVATE-FIRST", "PRIVATE-LAST", "12345678901", "1990",
            "PRIVATE-PNR", "PRIVATE-VOUCHER", "11111111-1111-4111-8111-111111111111", "easyticket", "3D Secure" })
            Assert.DoesNotContain(forbidden, rendered.Html + rendered.Text, StringComparison.Ordinal);
        email.Ticket.CustomerName = "Deneme Misafir"; email.Ticket.TourName = "Sunset";
        var preview = CancellationEmailTemplate.Render(email.Ticket);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"cancellation-email-preview-{language}-{operation}.html"), preview.Html);
    }

    [Fact]
    public async Task MimeMessageUsesBookingContactOnlyAndCancellationTimestamp()
    {
        var email = await Queue();
        var sender = new SmtpPaymentEmailSender(Options.Create(new MailOptions { Username = "fixture", Password = "not-real-secret",
            SenderEmail = "sender@example.test", SenderName = "Dentur Pereme" }));
        var message = sender.CreateCancellationMessage(email);
        Assert.Equal(email.Ticket.CustomerEmail, Assert.Single(message.To.Mailboxes).Address);
        Assert.Empty(message.Cc); Assert.Empty(message.Bcc);
        Assert.Equal(_clock.GetUtcNow(), message.Date);
        Assert.StartsWith("pereme-cancellation-", message.MessageId, StringComparison.Ordinal);
        Assert.DoesNotContain("not-real-secret", message.HtmlBody + message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoWorkersCannotSendTheSameQueuedEmail()
    {
        await Queue(); _sender.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Processor().ProcessNextAsync(CancellationToken.None);
        using var second = Context(); Assert.False(await Processor(second).ProcessNextAsync(CancellationToken.None));
        _sender.Gate.SetResult(); Assert.True(await first); Assert.Equal(1, _sender.Calls);
    }

    [Fact]
    public async Task TransientSmtpFailureRetriesOnlyEmailAndStopsAfterFourAttempts()
    {
        await Queue(); _sender.Failure = new EmailDeliveryException("SMTP_CONNECT_FAILED", "PRIVATE-ERROR", canRetry: true);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
            _db.ChangeTracker.Clear(); var email = await _db.CancellationEmails.SingleAsync();
            Assert.Equal(attempt, email.AttemptCount);
            Assert.Equal(attempt == 4 ? PaymentEmailStatus.Failed : PaymentEmailStatus.Queued, email.Status);
            _clock.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(4, _sender.Calls);
        Assert.Equal(TicketPaymentStatus.Refunded, (await _db.TourTickets.AsNoTracking().SingleAsync()).PaymentStatus);
        Assert.Equal(TicketCancellationStatus.Completed, (await _db.TicketCancellations.AsNoTracking().SingleAsync()).Status);
        var error = await _db.TicketErrorRecords.AsNoTracking().FirstAsync();
        Assert.Equal(TicketErrorStage.Email, error.Stage); Assert.DoesNotContain("PRIVATE-ERROR", error.Message, StringComparison.Ordinal);
        Assert.Equal(PaymentEmailStatus.Failed, Assert.Single((await new TicketErrorService(_db).ListAsync(null, TicketErrorStage.Email, 1, 1, CancellationToken.None)).Items).EmailStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSmtpResultsNeverAutomaticallyResend(bool unsafeCode)
    {
        await Queue(); _sender.Failure = unsafeCode
            ? new EmailDeliveryException("password=PRIVATE-PASSWORD", "PAN=4111111111111111", canRetry: true)
            : new EmailDeliveryException("SMTP_RESULT_UNKNOWN", "PAN=4111111111111111", isAmbiguous: true);
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None)); _clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(1, _sender.Calls);
        var email = await _db.CancellationEmails.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentEmailStatus.ReviewRequired, email.Status); Assert.Equal("CANCELLATION_EMAIL_SMTP_RESULT_UNKNOWN", email.LastFailureCode);
        var error = await _db.TicketErrorRecords.SingleAsync();
        Assert.DoesNotContain("PRIVATE-PASSWORD", error.Message + error.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("4111111111111111", error.Message + error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleWorkerIsMarkedForReviewWithoutSendingAgain()
    {
        await Queue(); var email = await _db.CancellationEmails.SingleAsync();
        email.Status = PaymentEmailStatus.Processing; email.LockToken = Guid.NewGuid(); email.LockedUntilUtc = _clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
        await _db.SaveChangesAsync(); _db.ChangeTracker.Clear();
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(0, _sender.Calls);
        Assert.Equal(PaymentEmailStatus.ReviewRequired, (await _db.CancellationEmails.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ExistingCancellationsAreNotBackfilledOrAutomaticallyNotified()
    {
        await Queue(createMail: false);
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(0, _sender.Calls);
        Assert.Empty(await _db.CancellationEmails.ToListAsync());
    }

    [Fact]
    public async Task SuccessfulSmtpAcknowledgementIsSavedEvenDuringShutdown()
    {
        await Queue(); using var stop = new CancellationTokenSource(); _sender.AfterSend = stop.Cancel;
        Assert.True(await Processor().ProcessNextAsync(stop.Token));
        Assert.Equal(PaymentEmailStatus.Sent, (await _db.CancellationEmails.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task LostLeaseCannotOverwriteAReviewDecisionOrSendAgain()
    {
        await Queue();
        _sender.AfterSend = () => _db.CancellationEmails.ExecuteUpdate(setters =>
            setters.SetProperty(item => item.Status, PaymentEmailStatus.ReviewRequired)
                .SetProperty(item => item.LockToken, (Guid?)null));
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        Assert.Equal(PaymentEmailStatus.ReviewRequired, (await _db.CancellationEmails.AsNoTracking().SingleAsync()).Status);
        Assert.False(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(1, _sender.Calls);
    }

    [Fact]
    public async Task RetryRechecksFreshBankStateEvenWhenTheScopePreviouslyTrackedIt()
    {
        await Queue(); _sender.Failure = new EmailDeliveryException("SMTP_CONNECT_FAILED", "Fixture failure", canRetry: true);
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None));
        var tracked = await _db.TicketCancellations.SingleAsync();
        Assert.Equal(TicketCancellationStatus.Completed, tracked.Status);
        await _db.TicketCancellations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, TicketCancellationStatus.ReviewRequired));
        _clock.Advance(TimeSpan.FromMinutes(2)); _sender.Failure = null;
        Assert.True(await Processor().ProcessNextAsync(CancellationToken.None)); Assert.Equal(1, _sender.Calls);
        Assert.Equal("CANCELLATION_EMAIL_NOT_CONFIRMED", (await _db.CancellationEmails.AsNoTracking().SingleAsync()).LastFailureCode);
    }

    public sealed class TestModel(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
        }
    }
    private sealed class Sender : ICancellationEmailSender
    {
        public int Calls { get; private set; }
        public EmailDeliveryException? Failure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public Action? AfterSend { get; set; }
        public async Task SendAsync(CancellationEmail email, CancellationToken cancellationToken)
        {
            Assert.True(CancellationEmailPolicy.IsConfirmed(email.Ticket)); Calls++;
            if (Gate is not null) await Gate.Task;
            if (Failure is not null) throw Failure;
            AfterSend?.Invoke();
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
