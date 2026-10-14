using System.Net;
using PeremeTours.Domain.Content;

namespace PeremeTours.Infrastructure.Email;

internal static class ContactReplyEmailTemplate
{
    public static RenderedPaymentEmail Render(ContactReply reply)
    {
        var contact = reply.Request;
        var english = contact.Language == "en";
        var subject = english ? "Dentur | Pereme Tours — Response to your message" : "Dentur | Pereme Tours — Mesajınıza yanıt";
        var title = english ? "We have responded to your message." : "Mesajınızı yanıtladık.";
        var greeting = english ? "Hello" : "Merhaba";
        var label = english ? "OUR RESPONSE" : "YANITIMIZ";
        var original = english ? "Your original message" : "Gönderdiğiniz mesaj";
        var footer = english ? "Thank you for contacting Dentur | Pereme Tours." : "Dentur | Pereme Tours ile iletişime geçtiğiniz için teşekkür ederiz.";
        static string Encode(string value) => WebUtility.HtmlEncode(value);
        static string Lines(string value) => Encode(value).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
        var html = $"""
            <!doctype html><html lang="{(english ? "en" : "tr")}"><body style="margin:0;background:#edf3fa;font-family:Arial,sans-serif;color:#071b35">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:32px 16px">
            <table role="presentation" width="100%" style="max-width:600px;background:white;border-radius:20px;overflow:hidden" cellpadding="0" cellspacing="0">
            <tr><td style="padding:28px;background:#1f478c;color:white;font-size:20px;font-weight:bold">DENTUR | PEREME TOURS</td></tr>
            <tr><td style="padding:30px;line-height:1.7"><p style="color:#6a9dd2;font-size:12px;letter-spacing:2px">{label}</p>
            <h1 style="font-size:25px;line-height:1.3">{title}</h1><p>{greeting} {Encode(contact.Name)},</p>
            <h2 style="font-size:17px">{Encode(contact.Topic)}</h2>
            <div style="background:#f2f6fc;border-left:3px solid #6a9dd2;border-radius:10px;padding:20px;overflow-wrap:anywhere">{Lines(reply.Body)}</div>
            <p>{footer}</p><a href="https://pereme.com.tr" style="display:inline-block;padding:12px 22px;background:#1f478c;border-radius:24px;color:white;text-decoration:none">pereme.com.tr</a>
            <h3 style="margin-top:28px;font-size:13px;color:#6a9dd2">{original}</h3><div style="font-size:13px;overflow-wrap:anywhere">{Lines(contact.Message)}</div>
            <p style="font-size:11px;color:#6a9dd2">{(english ? "Request" : "Talep")}: {contact.Id}</p>
            </td></tr></table></td></tr></table></body></html>
            """;
        var text = $"{subject}\n\n{greeting} {contact.Name},\n\n{contact.Topic}\n\n{reply.Body}\n\n{footer}\nhttps://pereme.com.tr\n\n{original}:\n{contact.Message}\n\n{(english ? "Request" : "Talep")}: {contact.Id}";
        return new(subject, html, text);
    }
}
