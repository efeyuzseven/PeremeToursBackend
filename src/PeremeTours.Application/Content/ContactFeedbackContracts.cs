using System.ComponentModel.DataAnnotations;
using PeremeTours.Domain.Content;

namespace PeremeTours.Application.Content;

public enum ContactFeedbackFilter { Unanswered, Answered }
public sealed record ContactFeedbackSummary(Guid Id, string Name, string Email, string Topic, string Language,
    DateTime CreatedAtUtc, int ReplyCount, ContactEmailStatus? LatestReplyStatus);
public sealed record ContactFeedbackPage(IReadOnlyList<ContactFeedbackSummary> Items, int TotalCount, int Page, int PageSize,
    int TotalRequests, int UnansweredRequests, int AnsweredRequests, bool EmailSendingEnabled);
public sealed record ContactReplySummary(Guid Id, int Revision, string Body, DateTime CreatedAtUtc,
    ContactEmailStatus Status, DateTime? SentAtUtc, string? FailureCode);
public sealed record ContactNotificationSummary(string RecipientEmail, ContactEmailStatus Status, DateTime? SentAtUtc, string? FailureCode);
public sealed record ContactFeedbackDetail(Guid Id, string Name, string Email, string Topic, string Message, string Language,
    DateTime CreatedAtUtc, int ReplyRevision, IReadOnlyList<ContactReplySummary> Replies, IReadOnlyList<ContactNotificationSummary> Notifications);
public sealed record ContactReplySubmission(Guid ReplyId, [Range(0, int.MaxValue)] int ExpectedRevision,
    [Required, StringLength(6000)] string Body);
public sealed record ContactReplyReceipt(Guid ReplyId, Guid RequestId, int Revision, ContactEmailStatus Status);
public sealed class ContactFeedbackConflictException : Exception;
public sealed class ContactFeedbackForbiddenException : Exception;
public sealed class ContactReplyBlockedException : Exception;

public interface IContactFeedbackService
{
    Task<ContactFeedbackPage> ListAsync(string? search, ContactFeedbackFilter? filter, int page, int pageSize, CancellationToken cancellationToken);
    Task<ContactFeedbackDetail?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ContactReplyReceipt?> ReplyAsync(Guid id, Guid adminId, ContactReplySubmission reply, CancellationToken cancellationToken);
}
