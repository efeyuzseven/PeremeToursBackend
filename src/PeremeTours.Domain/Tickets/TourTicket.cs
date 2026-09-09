using PeremeTours.Domain.Users;

namespace PeremeTours.Domain.Tickets;

public sealed class TourTicket
{
    public Guid Id { get; set; }

    public required string TicketCode { get; set; }

    public required string TourName { get; set; }

    public DateOnly TourDate { get; set; }

    public TimeOnly DepartureTime { get; set; }

    public required string CustomerName { get; set; }

    public required string CustomerEmail { get; set; }

    public int GuestCount { get; set; }

    public decimal Amount { get; set; }

    public required string Currency { get; set; }

    public TicketStatus Status { get; set; }

    public TicketChannel Channel { get; set; }

    public Guid? UserId { get; set; }

    public UserAccount? User { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
