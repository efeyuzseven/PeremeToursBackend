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
    bool CanCancel = false
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

public interface ITicketService
{
    Task<IReadOnlyList<TicketSummary>> ListAsync(
        CancellationToken cancellationToken
    );

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
