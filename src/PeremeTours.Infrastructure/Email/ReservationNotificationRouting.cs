using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal static class ReservationNotificationRouting
{
    public static IReadOnlyList<ReservationNotification> Create(TourTicket ticket, MailOptions options, DateTimeOffset now)
    {
        if (ticket.PaymentStatus != TicketPaymentStatus.Paid || ticket.TicketingStatus != TicketingStatus.Issued
            || ticket.Status != TicketStatus.Confirmed || ticket.Cancellation is not null) return [];
        return RecipientsFor(ticket.TourCategoryKey, options).Select(recipient => new ReservationNotification
            {
                Id = Guid.NewGuid(), TicketId = ticket.Id, RecipientEmail = recipient,
                Status = ReservationNotificationStatus.Queued, CreatedAtUtc = now.UtcDateTime,
                NextAttemptAtUtc = now.UtcDateTime,
            }).ToList();
    }

    public static bool IsCurrentRecipient(ReservationNotification notification, MailOptions options)
    {
        var recipient = notification.RecipientEmail.Trim();
        return RecipientsFor(notification.Ticket.TourCategoryKey, options)
            .Any(expected => string.Equals(expected, recipient, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> RecipientsFor(string? category, MailOptions options)
    {
        // Use only the trusted category snapshot. Editable tour names cannot select a recipient.
        var recipients = category switch
        {
            TourCategoryKeys.TurkishNight => options.TurkishNightRecipients,
            TourCategoryKeys.Bosphorus => options.BosphorusRecipients,
            TourCategoryKeys.Sunset or TourCategoryKeys.Daytime => options.SunsetDaytimeRecipients,
            _ => [],
        };
        // One outbox row per address: (TicketId, RecipientEmail) is unique, so duplicates are collapsed.
        return recipients.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant()).Distinct().ToList();
    }
}
