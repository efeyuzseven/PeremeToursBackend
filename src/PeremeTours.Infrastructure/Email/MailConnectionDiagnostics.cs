using Microsoft.Extensions.Options;

namespace PeremeTours.Infrastructure.Email;

public static class MailConnectionDiagnostics
{
    public static async Task<string> CheckAsync(MailOptions options, CancellationToken cancellationToken, bool authenticate = false)
    {
        try
        {
            await new SmtpPaymentEmailSender(Options.Create(options)).CheckConnectionAsync(cancellationToken, authenticate);
            if (options.SecurityMode == MailSecurityMode.None)
                return authenticate ? "SMTP_PLAINTEXT_AUTH_OK" : "SMTP_PLAINTEXT_OK";
            return authenticate ? "SMTP_TLS_AUTH_OK" : "SMTP_TLS_OK";
        }
        catch (EmailDeliveryException exception) { return exception.Code; }
        catch { return "SMTP_CHECK_FAILED"; }
    }
}
