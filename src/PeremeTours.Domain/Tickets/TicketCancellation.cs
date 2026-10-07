namespace PeremeTours.Domain.Tickets;

public enum TicketCancellationStatus { Processing, ProviderCancelled, BankReversalStarted, Completed, ReviewRequired }

public sealed class TicketCancellation
{
    public Guid TicketId { get; set; }
    public TourTicket Ticket { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public required string Reason { get; set; }
    public decimal Amount { get; set; }
    public TicketCancellationStatus Status { get; set; }
    public string? BankOperation { get; set; }
    public string? BankTransactionId { get; set; }
    public string? BankReversalTransactionId { get; set; }
    public string? FailureCode { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ProviderCancelledAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    // These codes are emitted only BEFORE the provider mutation. Never reopen an
    // ambiguous cancellation/refund, a completed claim, or a crashed in-flight claim.
    public bool CanRetryPrecheck() => Status == TicketCancellationStatus.ReviewRequired
        && (FailureCode is "PROVIDER_CANCELLATION_CHECK_FAILED" or "PROVIDER_CANCELLATION_UNAVAILABLE")
        && ProviderCancelledAtUtc is null && BankReversalTransactionId is null && CompletedAtUtc is null;

    public TicketCancellationStatus DisplayStatus(DateTime now) =>
        Status is not (TicketCancellationStatus.Completed or TicketCancellationStatus.ReviewRequired)
            && UpdatedAtUtc < now.AddMinutes(-5) ? TicketCancellationStatus.ReviewRequired : Status;
}
