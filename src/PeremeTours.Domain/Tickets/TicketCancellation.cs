namespace PeremeTours.Domain.Tickets;

public enum TicketCancellationStatus { Processing, ProviderCancelled, BankReversalStarted, Completed, ReviewRequired }
public enum ProviderCancellationStatus { NotRequested, Queued, Processing, Cancelled, ReviewRequired }

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
    public DateTime? BankReversalStartedAtUtc { get; set; }
    public ProviderCancellationStatus ProviderStatus { get; set; }
    public string? ProviderFailureCode { get; set; }
    public int ProviderAttemptCount { get; set; }
    public DateTime? ProviderNextAttemptAtUtc { get; set; }
    public DateTime? ProviderLockedUntilUtc { get; set; }

    // Legacy provider-first failures happened before any bank reversal. They may
    // resume ONLY the bank leg. An unknown old provider POST must never be replayed.
    public bool CanResumeBankCancellation() => Status == TicketCancellationStatus.ReviewRequired
        && (FailureCode is "PROVIDER_CANCELLATION_CHECK_FAILED" or "PROVIDER_CANCELLATION_UNAVAILABLE"
            or "PROVIDER_CANCELLATION_UNKNOWN" or "PROVIDER_CANCELLATION_REJECTED")
        && BankReversalStartedAtUtc is null && BankReversalTransactionId is null && CompletedAtUtc is null;

    public TicketCancellationStatus DisplayStatus(DateTime now) =>
        Status is not (TicketCancellationStatus.Completed or TicketCancellationStatus.ReviewRequired)
            && UpdatedAtUtc < now.AddMinutes(-5) ? TicketCancellationStatus.ReviewRequired : Status;
}
