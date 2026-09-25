namespace PeremeTours.Domain.Tickets;

public enum TicketPaymentStatus
{
    NotRequired,
    Pending,
    Processing,
    Paid,
    Failed,
    Refunded,
}
