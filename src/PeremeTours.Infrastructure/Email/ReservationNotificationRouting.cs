using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal static class ReservationNotificationRouting
{
    public static IReadOnlyList<ReservationNotification> Create(TourTicket ticket, MailOptions options, DateTimeOffset now)
    {
        if (ticket.PaymentStatus != TicketPaymentStatus.Paid || ticket.TicketingStatus != TicketingStatus.Issued
            || ticket.Status != TicketStatus.Confirmed || ticket.Cancellation is not null) return [];
        var recipient = RecipientFor(ticket.TourCategoryKey, options);
        if (recipient is null) return [];
        return [new ReservationNotification
            {
                Id = Guid.NewGuid(), TicketId = ticket.Id, RecipientEmail = recipient,
                Status = ReservationNotificationStatus.Queued, CreatedAtUtc = now.UtcDateTime,
                NextAttemptAtUtc = now.UtcDateTime,
            }];
    }

    public static bool IsCurrentRecipient(ReservationNotification notification, MailOptions options)
    {
        var expected = RecipientFor(notification.Ticket.TourCategoryKey, options);
        return expected is not null && string.Equals(expected, notification.RecipientEmail.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? RecipientFor(string? category, MailOptions options)
    {
        // Use only the trusted category snapshot. Editable tour names cannot select a recipient.
        var recipient = category switch
        {
            TourCategoryKeys.TurkishNight => options.ReservationRecipient,
            TourCategoryKeys.Bosphorus => options.BosphorusRecipient,
            TourCategoryKeys.Sunset or TourCategoryKeys.Daytime => options.SunsetDaytimeRecipient,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(recipient) ? null : recipient.Trim().ToLowerInvariant();
    }
}
