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
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
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
