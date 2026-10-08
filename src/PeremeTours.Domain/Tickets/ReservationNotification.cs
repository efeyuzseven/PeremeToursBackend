namespace PeremeTours.Domain.Tickets;

public enum ReservationNotificationStatus { Queued, Processing, Sent, Failed, ReviewRequired, Skipped }

// Separate delivery per operational recipient. Customer receipt state and payment state are unaffected.
public sealed class ReservationNotification
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public TourTicket Ticket { get; set; } = null!;
    public required string RecipientEmail { get; set; }
    public ReservationNotificationStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public Guid? LockToken { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
}
