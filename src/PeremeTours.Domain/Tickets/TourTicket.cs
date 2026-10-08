using PeremeTours.Domain.Users;

namespace PeremeTours.Domain.Tickets;

public sealed class TourTicket
{
    public Guid Id { get; set; }

    public required string TicketCode { get; set; }

    public required string TourName { get; set; }

    // Trusted category snapshot from the validated provider catalogue; never inferred from a display title.
    public string? TourCategoryKey { get; set; }

    public DateOnly TourDate { get; set; }

    public TimeOnly DepartureTime { get; set; }

    public required string CustomerName { get; set; }

    public required string CustomerEmail { get; set; }

    public string CustomerLanguage { get; set; } = "tr";

    public string DeparturePortName { get; set; } = string.Empty;

    public PaymentEmail? PaymentEmail { get; set; }
    public TicketCancellation? Cancellation { get; set; }

    public string? CustomerPhone { get; set; }

    public int GuestCount { get; set; }

    public decimal Amount { get; set; }

    public required string Currency { get; set; }

    public TicketStatus Status { get; set; }

    public TicketChannel Channel { get; set; }

    public TicketPaymentStatus PaymentStatus { get; set; }

    public Guid? PaymentAttemptId { get; set; }

    public TicketingStatus TicketingStatus { get; set; }

    public string? ExternalVoucherGuid { get; set; }

    public string? TicketingFailureCode { get; set; }

    public List<TourPassenger> Passengers { get; set; } = [];

    public int? ExternalTourId { get; set; }

    public int? ExternalDeparturePortId { get; set; }

    public int? ExternalDepartureId { get; set; }

    public int? ExternalTripId { get; set; }

    public int? ExternalPriceId { get; set; }

    public string? PaymentProvider { get; set; }

    public string? BankAuthCode { get; set; }

    public string? BankHostReference { get; set; }

    public string? PaymentFailureCode { get; set; }

    public string? PaymentFailureMessage { get; set; }

    public DateTimeOffset? PaidAtUtc { get; set; }

    public Guid? UserId { get; set; }

    public UserAccount? User { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
