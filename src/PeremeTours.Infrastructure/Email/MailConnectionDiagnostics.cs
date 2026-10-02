using Microsoft.Extensions.Options;

namespace PeremeTours.Infrastructure.Email;

public static class MailConnectionDiagnostics
{
    public static async Task<string> CheckAsync(MailOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await new SmtpPaymentEmailSender(Options.Create(options)).CheckConnectionAsync(cancellationToken);
            return "SMTP_TLS_OK";
        }
        catch (EmailDeliveryException exception) { return exception.Code; }
        catch { return "SMTP_CHECK_FAILED"; }
    }
}
