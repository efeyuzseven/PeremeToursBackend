namespace PeremeTours.Domain.Content;

public sealed class ContactRequest
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public required string Topic { get; set; }
    public required string Message { get; set; }
    public required string Language { get; set; }
    public required string PayloadHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int ReplyRevision { get; set; }
    public List<ContactEmail> Emails { get; set; } = [];
    public List<ContactReply> Replies { get; set; } = [];
}

public sealed class ContactReply
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public ContactRequest Request { get; set; } = null!;
    public Guid AdminUserId { get; set; }
    public int Revision { get; set; }
    public required string Body { get; set; }
    public required string RecipientEmail { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public ContactEmailStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
    public Guid? LockToken { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
}

public enum ContactEmailStatus { Queued, Processing, Sent, Failed, ReviewRequired }

public sealed class ContactEmail
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public ContactRequest Request { get; set; } = null!;
    public required string RecipientEmail { get; set; }
    public ContactEmailStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
    public Guid? LockToken { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
}
