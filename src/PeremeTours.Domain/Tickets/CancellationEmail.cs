namespace PeremeTours.Domain.Tickets;

// Separate from the purchase receipt. Never stores card data or rendered customer content.
public sealed class CancellationEmail
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
