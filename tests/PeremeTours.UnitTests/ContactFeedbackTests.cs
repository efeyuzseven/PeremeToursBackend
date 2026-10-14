using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Content;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.UnitTests;

public sealed class ContactFeedbackTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly MailOptions _mail = new() { Enabled = true, Username = "fixture", Password = "fixture", SenderEmail = "sender@example.test", SenderName = "Dentur | Pereme Tours" };
    public ContactFeedbackTests()
    {
        _connection.Open(); _db = Context(); _db.Database.EnsureCreated();
        _db.Users.Add(new UserAccount { Id = _adminId, Email = "admin@example.test", NormalizedEmail = "ADMIN@EXAMPLE.TEST", FirstName = "Fixture",
            PasswordHash = "fixture-not-a-live-password", Role = UserRoles.Admin, IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        _db.SaveChanges(); _db.ChangeTracker.Clear();
    }
    private PeremeToursDbContext Context() => new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
        .ReplaceService<IModelCustomizer, CancellationEmailTests.TestModel>().Options);
    private ContactFeedbackService Service() => new(_db, Options.Create(_mail), TimeProvider.System);
    private async Task<Guid> Contact(string language = "tr", string name = "Fixture customer")
    {
        var id = Guid.NewGuid();
        await new ContactRequestService(_db, Options.Create(_mail), TimeProvider.System).SubmitAsync(new(id, name,
            "original-contact@example.test", "Fixture <topic>", "Original <script> message\nSecond line", language), CancellationToken.None);
        _db.ChangeTracker.Clear(); return id;
    }
    private static ContactReplySubmission Answer(int revision = 0, string body = "Fixture reply <script>\nSecond line", Guid? id = null) => new(id ?? Guid.NewGuid(), revision, body);
    private Task<ContactReplyReceipt?> Reply(Guid id, ContactReplySubmission? answer = null) => Service().ReplyAsync(id, _adminId, answer ?? Answer(), CancellationToken.None);

    [Fact]
    public async Task ExistingRequestsAreListedWithSearchFiltersPaginationAndPrivateDetail()
    {
        var first = await Contact(name: "Alpha customer"); var second = await Contact(name: "Beta customer");
        await Reply(first);
        var page = await Service().ListAsync("ALPHA", null, 1, 20, CancellationToken.None);
        Assert.Equal(first, Assert.Single(page.Items).Id); Assert.Equal(2, page.TotalRequests); Assert.Equal(1, page.UnansweredRequests);
        Assert.Equal(1, page.AnsweredRequests); Assert.Equal(ContactEmailStatus.Queued, page.Items[0].LatestReplyStatus);
        var unanswered = await Service().ListAsync(null, ContactFeedbackFilter.Unanswered, 1, 20, CancellationToken.None);
        Assert.Equal(second, Assert.Single(unanswered.Items).Id);
        var answered = await Service().ListAsync(null, ContactFeedbackFilter.Answered, 1, 20, CancellationToken.None); Assert.Equal(first, Assert.Single(answered.Items).Id);
        var paginated = await Service().ListAsync(null, null, 2, 1, CancellationToken.None); Assert.Single(paginated.Items); Assert.Equal(2, paginated.TotalCount);
        var detail = await Service().GetAsync(first, CancellationToken.None);
        Assert.Equal("Original <script> message\nSecond line", detail!.Message); Assert.Equal(2, detail.Notifications.Count); Assert.Single(detail.Replies);
        Assert.Null(await Service().GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task AnswerIsItsOwnDurableDeliveryJobAndRetriesReuseTheSameRecord()
    {
        var id = await Contact(); var answer = Answer(body: "  Fixture response  ");
        var created = await Reply(id, answer); var duplicate = await Reply(id, answer with { ExpectedRevision = 99 });
        Assert.Equal(created, duplicate); Assert.Equal(ContactEmailStatus.Queued, created!.Status);
        var job = await _db.ContactReplies.AsNoTracking().SingleAsync(); Assert.Equal("Fixture response", job.Body);
        Assert.Equal("original-contact@example.test", job.RecipientEmail); Assert.Equal(_adminId, job.AdminUserId); Assert.Equal(1, job.Revision);
        Assert.Equal(1, (await _db.ContactRequests.AsNoTracking().SingleAsync()).ReplyRevision);
        Assert.Equal(2, await _db.ContactEmails.CountAsync()); Assert.Empty(await _db.TourTickets.ToListAsync());
        await Assert.ThrowsAsync<ContactFeedbackConflictException>(() => Reply(id, answer with { Body = "Changed" }));
        Assert.Equal(1, await _db.ContactReplies.CountAsync());
    }

    [Fact]
    public async Task UnchangedPreviouslySentAnswerDoesNotSendAnotherEmail()
    {
        var id = await Contact(); var first = await Reply(id);
        await _db.ContactReplies.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.Sent));
        var second = await Reply(id, Answer(1)); Assert.Equal(first!.ReplyId, second!.ReplyId);
        Assert.Equal(ContactEmailStatus.Sent, second.Status); Assert.Equal(1, await _db.ContactReplies.CountAsync());
    }

    [Theory]
    [InlineData(ContactEmailStatus.Queued)][InlineData(ContactEmailStatus.Processing)][InlineData(ContactEmailStatus.ReviewRequired)]
    public async Task PendingOrUncertainDeliveryCannotStartANewAnswer(ContactEmailStatus status)
    {
        var id = await Contact(); await Reply(id);
        await _db.ContactReplies.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, status));
        await Assert.ThrowsAsync<ContactReplyBlockedException>(() => Reply(id, Answer(1, "Different response")));
        Assert.Equal(1, await _db.ContactReplies.CountAsync()); Assert.Equal(1, (await _db.ContactRequests.AsNoTracking().SingleAsync()).ReplyRevision);
    }

    [Fact]
    public async Task StaleAdministratorDraftCannotOverwriteOrAddToANewerReply()
    {
        var id = await Contact(); await Reply(id);
        await _db.ContactReplies.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.Sent));
        await Assert.ThrowsAsync<ContactFeedbackConflictException>(() => Reply(id, Answer(0, "Stale response")));
        var next = await Reply(id, Answer(1, "Fresh response")); Assert.Equal(2, next!.Revision); Assert.Equal(2, await _db.ContactReplies.CountAsync());
    }

    [Fact]
    public async Task OnlyAnExplicitNewAttemptRetriesAKnownFailedReply()
    {
        var id = await Contact(); var answer = Answer(); await Reply(id, answer);
        await _db.ContactReplies.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.Failed));
        var same = await Reply(id, answer); Assert.Equal(ContactEmailStatus.Failed, same!.Status); Assert.Equal(1, await _db.ContactReplies.CountAsync());
        var explicitRetry = await Reply(id, Answer(1)); Assert.Equal(2, explicitRetry!.Revision); Assert.Equal(ContactEmailStatus.Queued, explicitRetry.Status);
    }

    [Theory]
    [InlineData("User", true)][InlineData("Admin", false)]
    public async Task CurrentActiveAdministratorIsRequiredEvenForIdempotentRetry(string role, bool active)
    {
        var id = await Contact(); var answer = Answer(); await Reply(id, answer);
        await _db.Users.Where(item => item.Id == _adminId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Role, role).SetProperty(item => item.IsActive, active));
        await Assert.ThrowsAsync<ContactFeedbackForbiddenException>(() => Reply(id, answer)); Assert.Equal(1, await _db.ContactReplies.CountAsync());
    }

    [Fact]
    public async Task DisabledMailAndMissingContactNeverReserveAReplyRevision()
    {
        var id = await Contact(); _mail.Enabled = false;
        await Assert.ThrowsAsync<ContactMailUnavailableException>(() => Reply(id));
        Assert.Equal(0, (await _db.ContactRequests.AsNoTracking().SingleAsync()).ReplyRevision); Assert.Empty(await _db.ContactReplies.ToListAsync());
        Assert.Null(await Reply(Guid.NewGuid()));
    }

    [Fact]
    public async Task MailWorkerSendsOnceAndDoesNotMutateInternalNotificationsOrTickets()
    {
        var id = await Contact(); await Reply(id); var sender = new Sender();
        var processor = new ContactReplyEmailProcessor(_db, sender, TimeProvider.System);
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None)); Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
        var reply = await _db.ContactReplies.AsNoTracking().SingleAsync(); Assert.Equal(ContactEmailStatus.Sent, reply.Status); Assert.NotNull(reply.SentAtUtc); Assert.Equal(1, sender.Calls);
        Assert.All(await _db.ContactEmails.AsNoTracking().ToListAsync(), item => Assert.Equal(ContactEmailStatus.Queued, item.Status)); Assert.Empty(await _db.TourTickets.ToListAsync());
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task AmbiguousOrStaleDeliveryRequiresReviewAndIsNeverReplayed(bool ambiguous)
    {
        var id = await Contact(); await Reply(id); var sender = new Sender { Failure = new EmailDeliveryException("SMTP_RESULT_UNKNOWN", "Fixture", isAmbiguous: true) };
        if (!ambiguous) await _db.ContactReplies.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, ContactEmailStatus.Processing).SetProperty(item => item.LockedUntilUtc, DateTime.UtcNow.AddMinutes(-5)));
        var processor = new ContactReplyEmailProcessor(_db, sender, TimeProvider.System);
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None)); Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(ambiguous ? 1 : 0, sender.Calls); Assert.Equal(ContactEmailStatus.ReviewRequired, (await _db.ContactReplies.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task KnownTransientFailureIsRetriedAtMostFourTimes()
    {
        var id = await Contact(); await Reply(id); var sender = new Sender { Failure = new EmailDeliveryException("SMTP_CONNECT_FAILED", "Fixture", canRetry: true) };
        var processor = new ContactReplyEmailProcessor(_db, sender, TimeProvider.System);
        for (var i = 0; i < 4; i++)
        {
            await _db.ContactReplies.Where(item => item.Status == ContactEmailStatus.Queued).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
            Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        }
        Assert.False(await processor.ProcessNextAsync(CancellationToken.None)); Assert.Equal(4, sender.Calls);
        Assert.Equal(ContactEmailStatus.Failed, (await _db.ContactReplies.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("tr", "Mesajınıza yanıt")][InlineData("en", "Response to your message")]
    public async Task ReplyMailIsLocalizedEscapedAndSentOnlyToTheOriginalContact(string language, string subject)
    {
        var id = await Contact(language, "Fixture <script>"); await Reply(id);
        var reply = await _db.ContactReplies.AsNoTracking().Include(item => item.Request).SingleAsync();
        var sender = new SmtpPaymentEmailSender(Options.Create(_mail)); var message = sender.CreateContactReplyMessage(reply);
        Assert.Contains(subject, message.Subject); Assert.Equal("original-contact@example.test", message.To.Mailboxes.Single().Address);
        Assert.Equal(_mail.SenderEmail, message.From.Mailboxes.Single().Address); Assert.Equal(_mail.SenderEmail, message.ReplyTo.Mailboxes.Single().Address);
        Assert.Empty(message.Cc); Assert.Empty(message.Bcc); Assert.Empty(message.Attachments);
        Assert.Contains("&lt;script&gt;", message.HtmlBody); Assert.DoesNotContain("<script>", message.HtmlBody); Assert.Contains("Original <script>", message.TextBody);
        Assert.DoesNotContain(_adminId.ToString(), message.HtmlBody); Assert.Equal(message.MessageId, sender.CreateContactReplyMessage(reply).MessageId);
        reply.RecipientEmail = "other@example.test"; Assert.Throws<EmailDeliveryException>(() => sender.CreateContactReplyMessage(reply));
    }

    private sealed class Sender : IContactReplyEmailSender
    {
        public int Calls { get; private set; }
        public EmailDeliveryException? Failure { get; init; }
        public Task SendAsync(ContactReply reply, CancellationToken cancellationToken) { Calls++; if (Failure is not null) throw Failure; return Task.CompletedTask; }
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
