namespace PeremeTours.Domain.Tickets;

public enum TicketErrorStage { Payment, Ticketing, Email, Cancellation }

public sealed class TicketErrorRecord
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public TourTicket Ticket { get; set; } = null!;
    public TicketErrorStage Stage { get; set; }
    public required string Code { get; set; }
    public string? ProviderCode { get; set; }
    public required string Message { get; set; }
    public bool IsHistorical { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
