namespace PeremeTours.Domain.Tickets;

public enum PaymentEmailStatus { Queued, Processing, Sent, Failed, ReviewRequired }

// One durable notification per bank-approved payment. No card data or rendered PII is copied here.
public sealed class PaymentEmail
{
    public Guid TicketId { get; set; }
    public TourTicket Ticket { get; set; } = null!;
    public PaymentEmailStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public Guid? LockToken { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
}
