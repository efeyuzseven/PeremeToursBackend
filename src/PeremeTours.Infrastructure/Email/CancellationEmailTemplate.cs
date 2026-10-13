using System.Globalization;
using System.Net;
using System.Text;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal static class CancellationEmailPolicy
{
    public static bool IsConfirmed(TourTicket ticket) => ticket.Status == TicketStatus.Cancelled
        && ticket.PaymentStatus == TicketPaymentStatus.Refunded && ticket.PaymentProvider == "Ziraat"
        && ticket.Currency == "TRY" && ticket.Amount > 0
        && ticket.Cancellation is { Status: TicketCancellationStatus.Completed, CompletedAtUtc: not null,
            FailureCode: null, BankOperation: "Void" or "Credit" } cancellation
        && cancellation.Amount == ticket.Amount && !string.IsNullOrWhiteSpace(cancellation.BankTransactionId);
}

internal static class CancellationEmailTemplate
{
    public static RenderedPaymentEmail Render(TourTicket ticket)
    {
        if (!CancellationEmailPolicy.IsConfirmed(ticket))
            throw new InvalidOperationException("Cancellation email requires a durably confirmed bank reversal.");
        var en = ticket.CustomerLanguage == "en";
        var credit = ticket.Cancellation!.BankOperation == "Credit";
        var culture = CultureInfo.GetCultureInfo(en ? "en-GB" : "tr-TR");
        var heading = en ? "Your reservation has been cancelled." : "Rezervasyonunuz iptal edildi.";
        var state = credit
            ? en ? "The bank has approved the refund of your payment." : "Banka, ödemenizin iade işlemini onayladı."
            : en ? "The bank has approved the cancellation of your payment." : "Banka, ödemenizin iptalini onayladı.";
        var bankNote = en ? "You can follow the update in your card transactions with your bank."
            : "Kart hareketlerindeki güncellemeyi bankanızdan takip edebilirsiniz.";
        var amountLabel = credit ? en ? "Refund amount" : "İade tutarı" : en ? "Cancelled amount" : "İptal edilen tutar";
        var amount = $"{ticket.Amount.ToString("N2", culture)} {ticket.Currency}";
        var rows = new (string Label, string Value)[]
        {
            (en ? "Booking reference" : "Rezervasyon numarası", ticket.TicketCode),
            (en ? "Experience" : "Tur", ticket.TourName),
            (en ? "Tour date & departure" : "Tur tarihi ve hareket saati",
                $"{ticket.TourDate.ToString("d MMMM yyyy", culture)} · {ticket.DepartureTime:HH:mm}"),
            (en ? "Departure port" : "Kalkış noktası", ticket.DeparturePortName),
            (en ? "Guests" : "Misafir sayısı", ticket.GuestCount.ToString(culture)),
        };
        var table = new StringBuilder();
        foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.Value)))
            table.Append(CultureInfo.InvariantCulture, $"<tr><td style='padding:13px 0;border-bottom:1px solid #e6edf7;color:#1f478c;font-size:13px;width:42%;vertical-align:top'>{WebUtility.HtmlEncode(row.Label)}</td><td style='padding:13px 0;border-bottom:1px solid #e6edf7;color:#071b35;font-size:14px;font-weight:bold;overflow-wrap:anywhere'>{WebUtility.HtmlEncode(row.Value)}</td></tr>");
        var contact = en ? "https://pereme.com.tr/contact" : "https://pereme.com.tr/iletisim";
        var help = en ? "Contact our team" : "Ekibimize ulaşın";
        var greeting = $"{(en ? "Hello" : "Merhaba")} {ticket.CustomerName},";
        var html = $"""
            <!doctype html><html lang="{(en ? "en" : "tr")}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#f3f7fc;font-family:Arial,sans-serif;color:#071b35">
            <table role="presentation" style="width:100%;padding:32px 16px"><tr><td align="center">
            <table role="presentation" style="width:100%;max-width:600px;background:#fff;border-radius:20px;overflow:hidden">
            <tr><td style="padding:30px;background:#071b35;color:#fff;font-size:24px;font-weight:bold">DENTUR <span style="color:#6a9dd2">pereme</span></td></tr>
            <tr><td style="padding:32px"><p style="color:#1f478c;font-size:14px">{WebUtility.HtmlEncode(greeting)}</p>
            <h1 style="font-size:28px;line-height:1.25;letter-spacing:-1px">{WebUtility.HtmlEncode(heading)}</h1>
            <p style="font-size:15px;line-height:1.7;color:#1f478c">{WebUtility.HtmlEncode(state)}</p>
            <table role="presentation" style="width:100%;border-collapse:collapse">{table}</table>
            <div style="margin:24px 0;padding:20px;border-radius:14px;background:#f3f7fc"><div style="font-size:13px;color:#1f478c">{WebUtility.HtmlEncode(amountLabel)}</div><div style="font-size:28px;font-weight:bold;margin-top:8px">{WebUtility.HtmlEncode(amount)}</div></div>
            <p style="font-size:13px;line-height:1.7;color:#1f478c">{WebUtility.HtmlEncode(bankNote)}</p>
            <p style="margin:24px 0 0"><a href="{contact}" style="display:inline-block;padding:14px 24px;border-radius:26px;background:#1f478c;color:#fff;text-decoration:none;font-weight:bold;font-size:14px">{help} →</a></p>
            </td></tr></table></td></tr></table></body></html>
            """;
        var text = $"{heading}\n\n{greeting}\n{state}\n\n"
            + string.Join("\n", rows.Where(row => !string.IsNullOrWhiteSpace(row.Value)).Select(row => $"{row.Label}: {row.Value}"))
            + $"\n\n{amountLabel}: {amount}\n{bankNote}\n\n{help}: {contact}";
        // No QR/voucher links, provider claims, passenger identity or card data in this receipt.
        return new(en ? $"Cancellation / Refund Confirmation | {ticket.TicketCode}"
            : $"İptal / İade Bildirimi | {ticket.TicketCode}", html, text);
    }
}
