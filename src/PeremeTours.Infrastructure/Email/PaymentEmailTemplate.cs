using System.Globalization;
using System.Net;
using System.Text;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal sealed record RenderedPaymentEmail(string Subject, string Html, string Text);

internal static class PaymentEmailTemplate
{
    public static RenderedPaymentEmail Render(TourTicket ticket, MailOptions options)
    {
        if (ticket.PaymentStatus != TicketPaymentStatus.Paid)
            throw new InvalidOperationException("A payment confirmation requires a verified paid order.");
        var en = ticket.CustomerLanguage == "en";
        var culture = CultureInfo.GetCultureInfo(en ? "en-GB" : "tr-TR");
        var issued = ticket.TicketingStatus == TicketingStatus.Issued;
        var heading = en ? "Your payment is complete." : "Ödemeniz tamamlandı.";
        var intro = en ? "Your next beautiful moment awaits on the Bosphorus." : "Sıradaki güzel anınız Boğaz’da sizi bekliyor.";
        var state = issued
            ? (en ? "Your tickets have been issued. Keep this email for your trip." : "Biletleriniz oluşturuldu. Yolculuğunuz için bu e-postayı saklayabilirsiniz.")
            : (en ? "Your payment was received. Our team is checking ticket issuance. Please do not make another payment." : "Ödemeniz alındı. Bilet kesimi ekibimiz tarafından kontrol ediliyor. Lütfen tekrar ödeme yapmayın.");
        var date = ticket.TourDate.ToString("d MMMM yyyy", culture);
        var time = ticket.DepartureTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        var amount = $"{ticket.Amount.ToString("N2", culture)} {ticket.Currency}";
        var rows = new (string Label, string Value)[]
        {
            (en ? "Booking reference" : "Rezervasyon numarası", ticket.TicketCode),
            (en ? "Experience" : "Tur", ticket.TourName),
            (en ? "Date & departure" : "Tarih ve hareket saati", $"{date} · {time}"),
            (en ? "Departure port" : "Kalkış noktası", ticket.DeparturePortName),
            (en ? "Guests" : "Misafir sayısı", ticket.GuestCount.ToString(culture)),
        };
        var htmlRows = new StringBuilder();
        var text = new StringBuilder().AppendLine(heading).AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{(en ? "Hello" : "Merhaba")} {ticket.CustomerName},").AppendLine(state).AppendLine();
        foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.Value)))
        {
            htmlRows.Append(CultureInfo.InvariantCulture, $"<tr><td style='padding:13px 12px 13px 0;border-bottom:1px solid #e6edf7;color:#1f478c;font-size:13px;width:42%;vertical-align:top'>{E(row.Label)}</td><td style='padding:13px 0;border-bottom:1px solid #e6edf7;color:#071b35;font-size:14px;font-weight:600;overflow-wrap:anywhere'>{E(row.Value)}</td></tr>");
            text.AppendLine(CultureInfo.InvariantCulture, $"{row.Label}: {row.Value}");
        }
        var pnrHtml = new StringBuilder();
        if (issued)
        {
            foreach (var passenger in ticket.Passengers.OrderBy(item => item.Sequence).Where(item => !string.IsNullOrWhiteSpace(item.Pnr)))
            {
                var label = en ? $"Guest {passenger.Sequence + 1} · PNR" : $"{passenger.Sequence + 1}. misafir · PNR";
                pnrHtml.Append(CultureInfo.InvariantCulture, $"<div style='margin-top:10px;padding:12px 16px;background:#f3f7fc;border:1px solid #dce8f5;border-radius:10px;font-size:13px;color:#1f478c;overflow-wrap:anywhere'>{E(label)}: <strong style='color:#071b35'>{E(passenger.Pnr!)}</strong></div>");
                text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {passenger.Pnr}");
            }
        }
        var url = Uri.TryCreate(options.WebsiteUrl, UriKind.Absolute, out var website) && website.Scheme == Uri.UriSchemeHttps
            ? website.AbsoluteUri : "https://d2bmjk2h6qp4lz.cloudfront.net/";
        var contactUrl = new Uri(new Uri(url), en ? "/contact" : "/iletisim").AbsoluteUri;
        var support = en ? "Questions about your trip? We’re here to help." : "Yolculuğunuzla ilgili bir sorunuz mu var? Yanınızdayız.";
        text.AppendLine(CultureInfo.InvariantCulture, $"{(en ? "Amount paid" : "Ödenen tutar")}: {amount}")
            .AppendLine().AppendLine(support).AppendLine(contactUrl)
            .AppendLine(en ? "Never share your card details by email." : "Kart bilgilerinizi e-posta ile paylaşmayın.");
        var html = $$"""
            <!doctype html><html lang="{{(en ? "en" : "tr")}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{E(heading)}}</title></head>
            <body style="margin:0;padding:0;background:#f3f7fc;font-family:Arial,Helvetica,sans-serif;color:#071b35">
            <div style="display:none;max-height:0;overflow:hidden;mso-hide:all">{{E(intro)}} {{E(state)}}</div>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0"><tr><td align="center" style="padding:32px 12px">
            <table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;background:#fff;border-radius:20px;overflow:hidden;border:1px solid #dce8f5">
            <tr><td style="padding:32px;background:#071b35;border-top:5px solid #6a9dd2"><div style="font-size:22px;font-weight:800;letter-spacing:2px;color:#fff">DENTUR <span style="color:#6a9dd2;font-weight:400">pereme</span></div><div style="margin-top:16px;color:#6a9dd2;font-size:11px;letter-spacing:2px">{{(en ? "THE BOSPHORUS, YOUR WAY" : "İSTANBUL’U DENİZDEN TANI")}}</div></td></tr>
            <tr><td style="padding:32px"><div style="display:inline-block;padding:8px 12px;background:#f3f7fc;border:1px solid #dce8f5;border-radius:20px;color:#1f478c;font-size:11px;font-weight:bold">✓ {{(en ? "PAYMENT RECEIVED" : "ÖDEME ALINDI")}}</div>
            <h1 style="margin:20px 0 10px;font-size:30px;line-height:1.2;letter-spacing:-1px;color:#071b35">{{E(heading)}}</h1>
            <p style="margin:0 0 24px;color:#6a9dd2;font-size:17px;line-height:1.6">{{E(intro)}}</p>
            <p style="font-size:14px;line-height:1.8;color:#1f478c">{{(en ? "Hello" : "Merhaba")}} <strong>{{E(ticket.CustomerName)}}</strong>,<br>{{E(state)}}</p>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin-top:20px">{{htmlRows}}</table>
            {{pnrHtml}}
            <table role="presentation" width="100%" style="margin-top:24px;background:#1f478c;border-radius:12px"><tr><td style="padding:20px;color:#fff;font-size:13px">{{(en ? "Amount paid" : "Ödenen tutar")}}</td><td align="right" style="padding:20px;color:#fff;font-size:23px;font-weight:bold">{{E(amount)}}</td></tr></table>
            <p style="margin:28px 0 14px;color:#1f478c;font-size:13px;line-height:1.6">{{E(support)}}</p>
            <a href="{{E(contactUrl)}}" style="display:inline-block;padding:14px 22px;background:#6a9dd2;border-radius:24px;color:#fff;text-decoration:none;font-size:13px;font-weight:bold">{{(en ? "Contact our team →" : "Ekibimize ulaşın →")}}</a></td></tr>
            <tr><td style="padding:24px 32px;border-top:1px solid #e6edf7;font-size:11px;line-height:1.8;color:#1f478c">{{(en ? "This email confirms payment; it is not an invoice." : "Bu e-posta ödeme bilgilendirmesidir; fatura yerine geçmez.")}}<br>{{(en ? "Never share your card details by email." : "Kart bilgilerinizi e-posta ile paylaşmayın.")}}<br><a href="{{E(url)}}" style="color:#1f478c;text-decoration:none">Dentur Pereme · İstanbul</a></td></tr>
            </table></td></tr></table></body></html>
            """;
        return new RenderedPaymentEmail($"{heading} | {ticket.TicketCode}", html, text.ToString());
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
