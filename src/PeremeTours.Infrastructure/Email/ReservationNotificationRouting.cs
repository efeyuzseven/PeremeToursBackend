using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal static class ReservationNotificationRouting
{
    public static IReadOnlyList<ReservationNotification> Create(TourTicket ticket, MailOptions options, DateTimeOffset now)
    {
        if (ticket.PaymentStatus != TicketPaymentStatus.Paid || ticket.TicketingStatus != TicketingStatus.Issued
            || ticket.Status != TicketStatus.Confirmed || ticket.Cancellation is not null) return [];
        var recipients = new List<string> { options.ReservationRecipient };
        if (ticket.TourCategoryKey is TourCategoryKeys.Sunset or TourCategoryKeys.Daytime)
            recipients.Add(options.SunsetDaytimeRecipient);
        return recipients.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => new ReservationNotification
            {
                Id = Guid.NewGuid(), TicketId = ticket.Id, RecipientEmail = value,
                Status = ReservationNotificationStatus.Queued, CreatedAtUtc = now.UtcDateTime,
                NextAttemptAtUtc = now.UtcDateTime,
            }).ToArray();
    }
}
