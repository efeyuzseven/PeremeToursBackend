namespace PeremeTours.Application.Tours;

public sealed record TourTicketSelection(int ExternalPriceId, int Quantity);

public sealed record TourQuoteCommand(
    int ExternalTourId,
    int ExternalDeparturePortId,
    int ExternalDepartureId,
    DateOnly TourDate,
    IReadOnlyList<TourTicketSelection> Tickets
);

public sealed record TourQuoteLine(
    int ExternalPriceId,
    string TicketType,
    string? TicketTypeEn,
    int Quantity,
    decimal UnitAmount,
    decimal Amount
);

// A fresh price check only; this does not reserve capacity or issue a ticket.
public sealed record TourQuote(
    int ExternalTourId,
    string TourName,
    int ExternalDeparturePortId,
    string PortName,
    int ExternalDepartureId,
    DateOnly TourDate,
    TimeOnly DepartureTime,
    IReadOnlyList<TourQuoteLine> Tickets,
    int GuestCount,
    decimal Amount,
    string Currency,
    DateTimeOffset CheckedAtUtc,
    int ExternalTripId = 0
);

public interface ITourBookingService
{
    Task<TourQuote> QuoteAsync(TourQuoteCommand command, CancellationToken cancellationToken);
}

public sealed class TourBookingValidationException(string message) : Exception(message);
