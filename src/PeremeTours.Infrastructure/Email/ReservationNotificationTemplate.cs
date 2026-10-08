using System.Globalization;
using System.Net;
using System.Text;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal static class ReservationNotificationTemplate
{
    public static RenderedPaymentEmail Render(TourTicket ticket, MailOptions options)
    {
        if (ticket.PaymentStatus != TicketPaymentStatus.Paid || ticket.TicketingStatus != TicketingStatus.Issued
            || ticket.Status != TicketStatus.Confirmed || ticket.Cancellation is not null)
            throw new InvalidOperationException("A reservation notification requires an active, paid and issued booking.");
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        var amount = $"{ticket.Amount.ToString("N2", culture)} {ticket.Currency}";
        var rows = new (string Label, string? Value)[]
        {
            ("Rezervasyon numarası", ticket.TicketCode),
            ("Tur", ticket.TourName),
            ("Tur tarihi", ticket.TourDate.ToString("d MMMM yyyy", culture)),
            ("Hareket saati", ticket.DepartureTime.ToString("HH:mm", CultureInfo.InvariantCulture)),
            ("Kalkış noktası", ticket.DeparturePortName),
            ("Misafir sayısı", ticket.GuestCount.ToString(culture)),
            ("İletişim adı", ticket.CustomerName),
            ("İletişim e-postası", ticket.CustomerEmail),
            ("İletişim telefonu", ticket.CustomerPhone),
        };
        var htmlRows = new StringBuilder();
        var text = new StringBuilder().AppendLine("Yeni rezervasyon")
            .AppendLine("Ödeme onaylandı ve biletler oluşturuldu.").AppendLine();
        foreach (var (label, value) in rows.Where(row => !string.IsNullOrWhiteSpace(row.Value)))
            AppendRow(label, value!, htmlRows, text);
        foreach (var passenger in ticket.Passengers.OrderBy(item => item.Sequence))
        {
            var label = $"{passenger.Sequence + 1}. bilet";
            var value = $"{passenger.TicketType ?? "Bilet"} · {passenger.UnitAmount.ToString("N2", culture)} {ticket.Currency}";
            if (!string.IsNullOrWhiteSpace(passenger.Pnr)) value += $" · PNR: {passenger.Pnr}";
            AppendRow(label, value, htmlRows, text);
        }
        var website = Uri.TryCreate(options.WebsiteUrl, UriKind.Absolute, out var configured) && configured.Scheme == Uri.UriSchemeHttps
            ? configured : new Uri("https://d2bmjk2h6qp4lz.cloudfront.net/");
        var adminUrl = new Uri(website, "/admin/tickets").AbsoluteUri;
        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Ödenen tutar: {amount}")
            .AppendLine(CultureInfo.InvariantCulture, $"Yönetim paneli: {adminUrl}")
            .AppendLine("Bu e-posta iç operasyon bildirimidir. Kimlik, pasaport, doğum tarihi ve kart bilgileri içermez.");
        var html = $$"""
            <!doctype html><html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Yeni rezervasyon</title></head>
            <body style="margin:0;background:#f3f7fc;font-family:Arial,Helvetica,sans-serif;color:#071b35">
            <div style="display:none;max-height:0;overflow:hidden;mso-hide:all">Yeni rezervasyon: {{E(ticket.TicketCode)}} · {{E(ticket.TourName)}}</div>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0"><tr><td align="center" style="padding:28px 12px">
            <table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;background:#fff;border:1px solid #dce8f5;border-radius:20px;overflow:hidden">
            <tr><td style="padding:28px;background:#071b35;border-top:5px solid #6a9dd2;color:#fff;font-size:22px;font-weight:bold">DENTUR <span style="color:#6a9dd2;font-weight:normal">Pereme Tours</span></td></tr>
            <tr><td style="padding:28px"><span style="display:inline-block;padding:8px 12px;background:#f3f7fc;border:1px solid #dce8f5;border-radius:20px;color:#1f478c;font-size:11px;font-weight:bold">YENİ REZERVASYON</span>
            <h1 style="margin:20px 0 10px;font-size:28px;color:#071b35">Yeni bir Boğaz yolculuğu.</h1>
            <p style="font-size:14px;line-height:1.7;color:#1f478c">Ödeme banka tarafından onaylandı ve biletler oluşturuldu. Rezervasyon özeti aşağıdadır.</p>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0">{{htmlRows}}</table>
            <table role="presentation" width="100%" style="margin-top:24px;background:#1f478c;border-radius:12px"><tr><td style="padding:18px;color:#fff;font-size:13px">Ödenen tutar</td><td align="right" style="padding:18px;color:#fff;font-size:23px;font-weight:bold">{{E(amount)}}</td></tr></table>
            <p style="margin:24px 0 0"><a href="{{E(adminUrl)}}" style="display:inline-block;padding:14px 22px;background:#6a9dd2;border-radius:24px;color:#fff;text-decoration:none;font-size:13px;font-weight:bold">Yönetim panelinde görüntüle →</a></p></td></tr>
            <tr><td style="padding:22px 28px;border-top:1px solid #e6edf7;font-size:11px;line-height:1.7;color:#1f478c">İç operasyon bildirimi · Dentur | Pereme Tours<br>Kimlik, pasaport, doğum tarihi ve kart bilgileri bu e-postaya eklenmez.</td></tr>
            </table></td></tr></table></body></html>
            """;
        return new RenderedPaymentEmail($"Yeni rezervasyon | {ticket.TicketCode}", html, text.ToString());
    }

    private static void AppendRow(string label, string value, StringBuilder html, StringBuilder text)
    {
        html.Append(CultureInfo.InvariantCulture, $"<tr><td style='padding:12px 12px 12px 0;border-bottom:1px solid #e6edf7;color:#1f478c;font-size:13px;width:42%;vertical-align:top'>{E(label)}</td><td style='padding:12px 0;border-bottom:1px solid #e6edf7;font-size:14px;font-weight:600;overflow-wrap:anywhere'>{E(value)}</td></tr>");
        text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {value}");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
