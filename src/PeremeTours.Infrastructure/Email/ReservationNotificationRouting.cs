using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal static class ReservationNotificationRouting
{
    public static IReadOnlyList<ReservationNotification> Create(TourTicket ticket, MailOptions options, DateTimeOffset now)
    {
        if (ticket.PaymentStatus != TicketPaymentStatus.Paid || ticket.TicketingStatus != TicketingStatus.Issued
            || ticket.Status != TicketStatus.Confirmed || ticket.Cancellation is not null) return [];
        return RecipientsFor(ticket, options).Select(recipient => new ReservationNotification
            {
                Id = Guid.NewGuid(), TicketId = ticket.Id, RecipientEmail = recipient,
                Status = ReservationNotificationStatus.Queued, CreatedAtUtc = now.UtcDateTime,
                NextAttemptAtUtc = now.UtcDateTime,
            }).ToList();
    }

    public static bool IsCurrentRecipient(ReservationNotification notification, MailOptions options)
        => RecipientsFor(notification.Ticket, options).Contains(Normalize(notification.RecipientEmail));

    // Accounting gets the e-invoice request version of the notification, with the customer's invoice details.
    public static bool IsInvoiceRequest(ReservationNotification notification, MailOptions options)
        => notification.Ticket.WantsEInvoice && NormalizeAll(options.InvoiceRequestRecipients).Contains(Normalize(notification.RecipientEmail));

    private static List<string> RecipientsFor(TourTicket ticket, MailOptions options)
    {
        // Use only the trusted category snapshot. Editable tour names cannot select a recipient.
        var recipients = ticket.TourCategoryKey switch
        {
            TourCategoryKeys.TurkishNight => options.TurkishNightRecipients,
            TourCategoryKeys.Bosphorus => options.BosphorusRecipients,
            TourCategoryKeys.Sunset or TourCategoryKeys.Daytime => options.SunsetDaytimeRecipients,
            _ => [],
        };
        if (ticket.WantsEInvoice) recipients = [.. recipients, .. options.InvoiceRequestRecipients];
        return NormalizeAll(recipients);
    }

    // One outbox row per address: (TicketId, RecipientEmail) is unique, so duplicates are collapsed.
    private static List<string> NormalizeAll(IEnumerable<string> recipients) => recipients.Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => Normalize(item)).Distinct().ToList();

    private static string Normalize(string recipient) => recipient.Trim().ToLowerInvariant();
}
