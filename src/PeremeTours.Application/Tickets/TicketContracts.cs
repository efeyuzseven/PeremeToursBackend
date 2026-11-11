using PeremeTours.Domain.Tickets;

namespace PeremeTours.Application.Tickets;

public sealed record TicketSummary(
    Guid Id,
    string TicketCode,
    string TourName,
    DateOnly TourDate,
    TimeOnly DepartureTime,
    string CustomerName,
    string CustomerEmail,
    int GuestCount,
    decimal Amount,
    string Currency,
    TicketStatus Status,
    TicketChannel Channel,
    TicketPaymentStatus PaymentStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    TicketingStatus TicketingStatus,
    string? ExternalVoucherGuid,
    string? TicketingFailureCode,
    string? PaymentFailureCode,
    PaymentEmailStatus? EmailStatus,
    DateTime? EmailSentAtUtc,
    string? CancellationStatus = null,
    string? CancellationFailureCode = null,
    bool CanCancel = false,
    bool CanRetryCancellation = false,
    PaymentEmailStatus? CancellationEmailStatus = null,
    DateTime? CancellationEmailSentAtUtc = null,
    TicketCancellationDetails? Cancellation = null
);

// Who started a cancellation/refund, why and when. Actor fields are null when the user no longer exists.
public sealed record TicketCancellationDetails(
    string? ActorName,
    string? ActorEmail,
    string Reason,
    DateTime RequestedAtUtc,
    DateTime? CompletedAtUtc
);

public sealed record CreateTicketCommand(
    string TourName,
    DateOnly TourDate,
    TimeOnly DepartureTime,
    string CustomerName,
    string CustomerEmail,
    int GuestCount,
    decimal Amount,
    TicketStatus Status,
    TicketChannel Channel
);

public sealed record UpdateTicketCommand(
    string? TourName,
    DateOnly? TourDate,
    TimeOnly? DepartureTime,
    string? CustomerName,
    string? CustomerEmail,
    int? GuestCount,
    decimal? Amount,
    TicketStatus? Status
);

// Filters for the admin sales spreadsheet. Month is the first day of the Istanbul sale month;
// Search matches ticket code, tour, customer name or email like the admin ticket list does.
public sealed record TicketExportQuery(DateOnly? Month, TicketStatus? Status, string? Search, bool Turkish);

public interface ITicketService
{
    Task<IReadOnlyList<TicketSummary>> ListAsync(
        CancellationToken cancellationToken
    );

    Task<byte[]> ExportAsync(TicketExportQuery query, CancellationToken cancellationToken);

    Task<TicketSummary> CreateAsync(
        Guid actorUserId,
        CreateTicketCommand command,
        CancellationToken cancellationToken
    );

    Task<TicketSummary?> UpdateAsync(
        Guid ticketId,
        UpdateTicketCommand command,
        CancellationToken cancellationToken
    );
}

public sealed class TicketUpdateValidationException(string message) : Exception(message);
