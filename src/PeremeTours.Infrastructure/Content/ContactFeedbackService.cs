using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

internal sealed class ContactFeedbackService(PeremeToursDbContext db, IOptions<MailOptions> mail, TimeProvider clock) : IContactFeedbackService
{
    public async Task<ContactFeedbackPage> ListAsync(string? search, ContactFeedbackFilter? filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var all = db.ContactRequests.AsNoTracking();
        var total = await all.CountAsync(cancellationToken);
        var unanswered = await all.CountAsync(item => !item.Replies.Any(), cancellationToken);
        var query = all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // EF translates these expressions to database LOWER/CONTAINS; culture/StringComparison overloads cannot be translated.
            query = query.Where(item => item.Name.ToLower().Contains(term) || item.Email.ToLower().Contains(term) || item.Topic.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }
        if (filter == ContactFeedbackFilter.Unanswered) query = query.Where(item => !item.Replies.Any());
        else if (filter == ContactFeedbackFilter.Answered) query = query.Where(item => item.Replies.Any());
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(item => item.CreatedAtUtc).ThenBy(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new ContactFeedbackSummary(item.Id, item.Name, item.Email, item.Topic, item.Language, item.CreatedAtUtc,
                item.Replies.Count, item.Replies.OrderByDescending(reply => reply.Revision).Select(reply => (ContactEmailStatus?)reply.Status).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        return new(items, count, page, pageSize, total, unanswered, total - unanswered, mail.Value.Enabled);
    }

    public async Task<ContactFeedbackDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var contact = await db.ContactRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (contact is null) return null;
        var replies = await db.ContactReplies.AsNoTracking().Where(item => item.RequestId == id).OrderBy(item => item.Revision)
            .Select(item => new ContactReplySummary(item.Id, item.Revision, item.Body, item.CreatedAtUtc, item.Status, item.SentAtUtc, item.LastFailureCode)).ToListAsync(cancellationToken);
        var notifications = await db.ContactEmails.AsNoTracking().Where(item => item.RequestId == id).OrderBy(item => item.RecipientEmail)
            .Select(item => new ContactNotificationSummary(item.RecipientEmail, item.Status, item.SentAtUtc, item.LastFailureCode)).ToListAsync(cancellationToken);
        return new(contact.Id, contact.Name, contact.Email, contact.Topic, contact.Message, contact.Language, contact.CreatedAtUtc, contact.ReplyRevision, replies, notifications);
    }

    public async Task<ContactReplyReceipt?> ReplyAsync(Guid id, Guid adminId, ContactReplySubmission submission, CancellationToken cancellationToken)
    {
        var body = submission.Body.Trim();
        // Check current permissions even for a duplicate request; the API additionally validates the JWT stamp.
        if (!await db.Users.AnyAsync(item => item.Id == adminId && item.IsActive && !item.IsDeleted
            && (item.Role == UserRoles.Admin || item.Role == UserRoles.Service), cancellationToken))
            throw new ContactFeedbackForbiddenException();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var existing = await db.ContactReplies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == submission.ReplyId, cancellationToken);
            if (existing is not null)
            {
                if (existing.RequestId != id || existing.Body != body) throw new ContactFeedbackConflictException();
                return Receipt(existing);
            }
            var contact = await db.ContactRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (contact is null) return null;
            var latest = await db.ContactReplies.AsNoTracking().Where(item => item.RequestId == id).OrderByDescending(item => item.Revision).FirstOrDefaultAsync(cancellationToken);
            // Like Dentur, saving an unchanged answer does not send the same email again.
            if (latest is not null && latest.Body == body && latest.Status != ContactEmailStatus.Failed) return Receipt(latest);
            if (latest?.Status is ContactEmailStatus.Queued or ContactEmailStatus.Processing or ContactEmailStatus.ReviewRequired)
                throw new ContactReplyBlockedException();
            if (contact.ReplyRevision != submission.ExpectedRevision) throw new ContactFeedbackConflictException();
            if (!mail.Value.Enabled) throw new ContactMailUnavailableException();
            var reserved = await db.ContactRequests.Where(item => item.Id == id && item.ReplyRevision == submission.ExpectedRevision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReplyRevision, item => item.ReplyRevision + 1), cancellationToken);
            if (reserved != 1) throw new ContactFeedbackConflictException();
            var now = clock.GetUtcNow().UtcDateTime;
            var reply = new ContactReply { Id = submission.ReplyId, RequestId = id, AdminUserId = adminId, Revision = submission.ExpectedRevision + 1,
                Body = body, RecipientEmail = contact.Email, CreatedAtUtc = now, Status = ContactEmailStatus.Queued, NextAttemptAtUtc = now };
            db.ContactReplies.Add(reply);
            // The answer IS the durable outbox job: no window exists where an answer is saved but its delivery job is lost.
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return Receipt(reply);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            var previous = await db.ContactReplies.AsNoTracking().SingleOrDefaultAsync(item => item.Id == submission.ReplyId, cancellationToken);
            if (previous is not null && previous.RequestId == id && previous.Body == body) return Receipt(previous);
            throw new ContactFeedbackConflictException();
        }
    }

    private static ContactReplyReceipt Receipt(ContactReply reply) => new(reply.Id, reply.RequestId, reply.Revision, reply.Status);
}
