namespace PeremeTours.Domain.Tickets;

public sealed class TourPassenger
{
    public Guid Id { get; set; }
    public Guid TourTicketId { get; set; }
    public int Sequence { get; set; }
    public int ExternalPriceId { get; set; }
    public string? TicketType { get; set; }
    public decimal UnitAmount { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Gender { get; set; }
    public required string Nationality { get; set; }
    public required string IdentityNumber { get; set; }
    public DateOnly BirthDate { get; set; }
    public string? ExternalTicketGuid { get; set; }
    public string? Pnr { get; set; }
}
