using System.Net;

namespace PeremeTours.Infrastructure.Email;

internal static class PasswordResetEmailTemplate
{
    public static RenderedPaymentEmail Render(string token, string language)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid reset token.");
        var en = language == "en";
        // Fixed public origin: Host, Origin, Referer and caller-supplied redirect URLs cannot alter the link.
        var link = $"https://pereme.com.tr/sifre-sifirla#token={token}";
        var heading = en ? "A fresh start for your account." : "Hesabın için yeni bir başlangıç.";
        var intro = en ? "Use the button below to set a new password for your Pereme account."
            : "Pereme hesabın için yeni bir şifre belirlemek üzere aşağıdaki butona tıklayabilirsin.";
        var label = en ? "Reset my password" : "Şifremi sıfırla";
        var expiry = en ? "This link is valid for 30 minutes and can only be used once."
            : "Bu bağlantı 30 dakika geçerlidir ve yalnızca bir kez kullanılabilir.";
        var disclaimer = en ? "Didn't request this? Ignore this email. Your password has not changed."
            : "Bu talebi sen yapmadıysan bu e-postayı dikkate almayabilirsin. Şifren değiştirilmedi.";
        var html = $"""
            <!doctype html><html lang="{(en ? "en" : "tr")}"><body style="margin:0;background:#f3f7fc;font-family:Arial,sans-serif;color:#071b35">
            <table role="presentation" style="width:100%;padding:32px 16px"><tr><td align="center">
            <table role="presentation" style="width:100%;max-width:560px;background:#fff;border-radius:20px;overflow:hidden">
            <tr><td style="padding:30px;background:#071b35;color:#fff;font-size:23px;font-weight:bold">DENTUR <span style="color:#6a9dd2">pereme</span></td></tr>
            <tr><td style="padding:32px"><h1 style="font-size:28px;letter-spacing:-1px">{WebUtility.HtmlEncode(heading)}</h1>
            <p style="font-size:15px;line-height:1.7;color:#1f478c">{WebUtility.HtmlEncode(intro)}</p>
            <p style="margin:28px 0"><a href="{WebUtility.HtmlEncode(link)}" style="display:inline-block;padding:16px 26px;border-radius:28px;background:#1f478c;color:#fff;text-decoration:none;font-weight:bold">{WebUtility.HtmlEncode(label)} →</a></p>
            <p style="font-size:13px;color:#1f478c;line-height:1.7">{WebUtility.HtmlEncode(expiry)}</p>
            <p style="font-size:12px;line-height:1.7;color:#1f478c;border-top:1px solid #e6edf7;padding-top:20px">{WebUtility.HtmlEncode(disclaimer)}</p>
            </td></tr></table></td></tr></table></body></html>
            """;
        return new(en ? "Reset your password | Dentur Pereme" : "Şifre sıfırlama | Dentur Pereme", html,
            $"{heading}\n\n{intro}\n\n{label}: {link}\n\n{expiry}\n\n{disclaimer}");
    }
}
