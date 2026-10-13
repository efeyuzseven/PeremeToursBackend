using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Content;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.UnitTests;

public sealed class HomepageAndContactTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly MailOptions _mail = new() { Enabled = true, SenderEmail = "sender@example.test", Username = "fixture", Password = "fixture" };
    public HomepageAndContactTests()
    {
        _connection.Open(); _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, CancellationEmailTests.TestModel>().Options);
        _db.Database.EnsureCreated();
    }
    private ContactRequestService Service() => new(_db, Options.Create(_mail), TimeProvider.System);
    private static ContactRequestSubmission Submission(Guid? id = null) => new(id ?? Guid.NewGuid(), "Fixture <script>", "customer@example.test", "Fixture & subject", "Hello <script>alert(1)</script>\nSecond line", "tr");

    [Fact]
    public async Task RequestAndTwoJobsArePersistedTogetherAndHttpRetryDoesNotDuplicate()
    {
        var request = Submission();
        await Service().SubmitAsync(request, CancellationToken.None);
        await Service().SubmitAsync(request with { Email = " CUSTOMER@example.test " }, CancellationToken.None);
        Assert.Single(await _db.ContactRequests.ToListAsync());
        var jobs = await _db.ContactEmails.ToListAsync(); Assert.Equal(2, jobs.Count);
        Assert.Equal(_mail.ContactRecipients.Order(), jobs.Select(item => item.RecipientEmail).Order());
        Assert.All(jobs, item => Assert.Equal(ContactEmailStatus.Queued, item.Status));
        Assert.Empty(await _db.ReservationNotifications.ToListAsync()); Assert.Empty(await _db.PaymentEmails.ToListAsync());
    }

    [Fact]
    public async Task ReusingAnIdWithDifferentContentsFailsWithoutOverwritingTheOriginal()
    {
        var request = Submission(); await Service().SubmitAsync(request, CancellationToken.None);
        await Assert.ThrowsAsync<ContactRequestConflictException>(() => Service().SubmitAsync(request with { Message = "Changed" }, CancellationToken.None));
        Assert.Equal(request.Message, (await _db.ContactRequests.SingleAsync()).Message); Assert.Equal(2, await _db.ContactEmails.CountAsync());
    }

    [Fact]
    public async Task DisabledMailDoesNotPretendToAcceptARequest()
    {
        _mail.Enabled = false;
        await Assert.ThrowsAsync<ContactMailUnavailableException>(() => Service().SubmitAsync(Submission(), CancellationToken.None));
        Assert.Empty(await _db.ContactRequests.ToListAsync());
    }

    [Fact]
    public async Task RecipientsAreIndependentAndAlreadySentJobsAreNotReplayed()
    {
        await Service().SubmitAsync(Submission(), CancellationToken.None);
        var sender = new Sender { FailFirst = true };
        var processor = new ContactEmailProcessor(_db, sender, TimeProvider.System);
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
        var jobs = await _db.ContactEmails.AsNoTracking().ToListAsync();
        Assert.Single(jobs, item => item.Status == ContactEmailStatus.Sent);
        Assert.Single(jobs, item => item.Status == ContactEmailStatus.Failed);
        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task AmbiguousSendAndExpiredLeaseNeverAutomaticallyResend()
    {
        await Service().SubmitAsync(Submission(), CancellationToken.None);
        var sender = new Sender { Ambiguous = true };
        var processor = new ContactEmailProcessor(_db, sender, TimeProvider.System);
        await processor.ProcessNextAsync(CancellationToken.None);
        await _db.ContactEmails.Where(item => item.Status == ContactEmailStatus.Queued).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, ContactEmailStatus.Processing).SetProperty(item => item.LockedUntilUtc, DateTime.UtcNow.AddMinutes(-1)));
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None)); Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(1, sender.Calls);
        Assert.All(await _db.ContactEmails.AsNoTracking().ToListAsync(), item => Assert.Equal(ContactEmailStatus.ReviewRequired, item.Status));
    }

    [Fact]
    public async Task ContactMimeUsesServerSenderFixedRecipientReplyToAndEscapedContent()
    {
        await Service().SubmitAsync(Submission(), CancellationToken.None);
        var sender = new SmtpPaymentEmailSender(Options.Create(_mail));
        var email = await _db.ContactEmails.Include(item => item.Request).FirstAsync();
        var message = sender.CreateContactMessage(email);
        Assert.Equal(_mail.SenderEmail, message.From.Mailboxes.Single().Address);
        Assert.Equal(email.RecipientEmail, message.To.Mailboxes.Single().Address);
        Assert.Equal("customer@example.test", message.ReplyTo.Mailboxes.Single().Address);
        Assert.Empty(message.Cc); Assert.Empty(message.Bcc); Assert.Empty(message.Attachments);
        Assert.Contains("&lt;script&gt;", message.HtmlBody); Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("<script>", message.TextBody); Assert.Contains("Second line", message.TextBody);
        Assert.Equal(message.MessageId, sender.CreateContactMessage(email).MessageId);
        email.RecipientEmail = "other@example.test";
        Assert.Throws<EmailDeliveryException>(() => sender.CreateContactMessage(email));
    }

    [Fact]
    public async Task TransientConnectionFailureRetriesOnlyUnsentRecipient()
    {
        await Service().SubmitAsync(Submission(), CancellationToken.None);
        var sender = new Sender { Transient = true };
        var processor = new ContactEmailProcessor(_db, sender, TimeProvider.System);
        await processor.ProcessNextAsync(CancellationToken.None);
        Assert.Equal(ContactEmailStatus.Queued, (await _db.ContactEmails.AsNoTracking().SingleAsync(item => item.AttemptCount == 1)).Status);
        sender.Transient = false;
        await processor.ProcessNextAsync(CancellationToken.None);
        await _db.ContactEmails.Where(item => item.Status == ContactEmailStatus.Queued).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        await processor.ProcessNextAsync(CancellationToken.None);
        Assert.All(await _db.ContactEmails.AsNoTracking().ToListAsync(), item => Assert.Equal(ContactEmailStatus.Sent, item.Status));
        Assert.Equal(3, sender.Calls);
    }

    [Fact]
    public async Task LegacyJsonAndLegacyEditorKeepNewImagesAndTranslations()
    {
        var original = HomepageContentDefaults.Value;
        var service = new HomepageContentService(_db);
        var customized = original with { Tr = original.Tr with { Hero = original.Tr.Hero with { Lead = "Existing custom content" },
                Texts = new Dictionary<string, string> { ["hero.eyebrow"] = "Custom label" } },
            Images = new Dictionary<string, string> { ["hero"] = "/assets/custom.webp" }, HiddenSections = ["stories"] };
        await service.UpdateAsync(customized, CancellationToken.None);
        var legacy = customized with { Tr = customized.Tr with { Texts = null }, Images = null, HiddenSections = null };
        var saved = await service.UpdateAsync(legacy, CancellationToken.None);
        Assert.Equal("/assets/custom.webp", saved.Images!["hero"]); Assert.Equal("Custom label", saved.Tr.Texts!["hero.eyebrow"]);
        Assert.Equal("Existing custom content", saved.Tr.Hero.Lead); Assert.Equal("stories", Assert.Single(saved.HiddenSections!));
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<HomepageContentDocument>(json, JsonOptions)!;
        Assert.Equal(original.Tr.Hero.Lead, deserialized.Tr.Hero.Lead); Assert.Null(deserialized.Images);
    }

    [Fact]
    public async Task HomepageImageUploadIsAnImmutableDraftAndDoesNotPublishOrReplaceContent()
    {
        var storage = new Storage();
        var service = new HomepageImageService(_db, storage, storage, TimeProvider.System);
        using var bytes = new MemoryStream([1, 2, 3]);
        var first = await service.UploadAsync(bytes, "image/png", CancellationToken.None);
        var second = await service.UploadAsync(bytes, "image/png", CancellationToken.None);
        Assert.NotEqual(first.Url, second.Url); Assert.Equal(2, await _db.HomepageImages.CountAsync());
        Assert.Empty(await _db.HomepageContents.ToListAsync());
        Assert.Contains("/api/v1/site-content/homepage/images/", first.Url);
        Assert.NotNull(await service.GetAsync(Guid.Parse(first.Url.Split('/').Last()), CancellationToken.None));
        Assert.Null(await service.GetAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, storage.Deletes);
    }

    private sealed class Sender : IContactEmailSender
    {
        public int Calls { get; private set; }
        public bool FailFirst { get; set; }
        public bool Ambiguous { get; set; }
        public bool Transient { get; set; }
        public Task SendAsync(ContactEmail email, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailFirst && Calls == 1) throw new EmailDeliveryException("SMTP_SEND_REJECTED", "Fixture");
            if (Ambiguous) throw new EmailDeliveryException("SMTP_RESULT_UNKNOWN", "Fixture", isAmbiguous: true);
            if (Transient) throw new EmailDeliveryException("SMTP_CONNECT_FAILED", "Fixture", canRetry: true);
            return Task.CompletedTask;
        }
    }
    private sealed class Storage : ITourImageStorage, ITourPageImageStorage
    {
        public int Deletes { get; private set; }
        public Task<string> SavePageImageAsync(string pageKey, Stream content, string contentType, CancellationToken cancellationToken)
            => Task.FromResult($"pages/{pageKey}/{Guid.NewGuid():N}.png");
        public Task<string> SaveAsync(int externalTourId, Stream content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TourImageFile?> GetAsync(string objectKey, string? contentType, CancellationToken cancellationToken) => Task.FromResult<TourImageFile?>(new([1, 2, 3], contentType!));
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) { Deletes++; return Task.CompletedTask; }
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
