using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Email;

internal interface IPaymentEmailSender
{
    Task SendAsync(TourTicket ticket, CancellationToken cancellationToken);
}

internal sealed class EmailDeliveryException(string code, string message, bool canRetry = false, bool isAmbiguous = false)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool CanRetry { get; } = canRetry;
    public bool IsAmbiguous { get; } = isAmbiguous;
}

internal sealed class SmtpPaymentEmailSender(IOptions<MailOptions> options) : IPaymentEmailSender
{
    private readonly MailOptions _options = options.Value;

    // Read-only TLS probe: no authentication and no email is sent.
    public async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        using var smtp = new SmtpClient { Timeout = 40_000 };
        await ConnectAsync(smtp, cancellationToken);
        await smtp.DisconnectAsync(true, cancellationToken);
    }

    public async Task SendAsync(TourTicket ticket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password)
            || !MailboxAddress.TryParse(_options.SenderEmail, out var sender))
            throw new EmailDeliveryException("SMTP_CONFIG_MISSING", "Mail sunucusu ayarları eksik; gönderim başlatılmadı.");
        if (!MailboxAddress.TryParse(ticket.CustomerEmail, out var recipient))
            throw new EmailDeliveryException("EMAIL_ADDRESS_INVALID", "Rezervasyon iletişim e-posta adresi geçersiz.");
        var content = PaymentEmailTemplate.Render(ticket, _options);
        var message = new MimeMessage
        {
            Subject = content.Subject,
            MessageId = $"pereme-payment-{ticket.Id:N}@{sender.Domain}",
            Date = ticket.PaidAtUtc ?? ticket.CreatedAtUtc,
            Body = new BodyBuilder { HtmlBody = content.Html, TextBody = content.Text }.ToMessageBody(),
        };
        message.From.Add(new MailboxAddress(_options.SenderName, sender.Address));
        message.To.Add(recipient);
        using var smtp = new SmtpClient { Timeout = 40_000 };
        await ConnectAsync(smtp, cancellationToken);
        try { await smtp.AuthenticateAsync(_options.Username, _options.Password, cancellationToken); }
        catch (AuthenticationException)
        { throw new EmailDeliveryException("SMTP_AUTH_FAILED", "Mail sunucusu kullanıcı doğrulamasını reddetti. SMTP hesabı kontrol edilmeli."); }
        catch (Exception exception) when (exception is SmtpCommandException or IOException or OperationCanceledException or SmtpProtocolException)
        { throw new EmailDeliveryException("SMTP_CONNECT_FAILED", "Mail sunucusuyla bağlantı tamamlanamadı; tekrar denenecek.", canRetry: true); }
        try { await smtp.SendAsync(message, cancellationToken); }
        catch (SmtpCommandException exception)
        {
            // An explicit SMTP rejection is known not to have delivered the message.
            var transient = (int)exception.StatusCode is >= 400 and < 500;
            throw new EmailDeliveryException("SMTP_SEND_REJECTED", "Mail sunucusu gönderimi kabul etmedi. Alıcı adresi ve SMTP gönderim izni kontrol edilmeli.", canRetry: transient);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SmtpProtocolException)
        { throw new EmailDeliveryException("SMTP_RESULT_UNKNOWN", "Mail gönderim sonucu belirsiz. Tekrar göndermeden önce mail sunucusu kaydı kontrol edilmeli.", isAmbiguous: true); }
        // A disconnect error after a successful SMTP acknowledgement must not trigger another send.
        try { await smtp.DisconnectAsync(true, cancellationToken); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SmtpProtocolException or SmtpCommandException) { }
    }

    private async Task ConnectAsync(SmtpClient smtp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Server) || _options.Port is not (465 or 587))
            throw new EmailDeliveryException("SMTP_CONFIG_MISSING", "Mail bağlantısı için 465/TLS veya 587/STARTTLS yapılandırılmalı.");
        try
        {
            // Certificate and hostname validation remain enabled. Never fall back to plaintext authentication.
            await smtp.ConnectAsync(_options.Server, _options.Port,
                _options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, cancellationToken);
        }
        catch (Exception exception) when (exception is SslHandshakeException or System.Security.Authentication.AuthenticationException or NotSupportedException)
        { throw new EmailDeliveryException("SMTP_TLS_FAILED", "Mail sunucusunun güvenli TLS bağlantısı doğrulanamadı. Sunucu sertifikası kontrol edilmeli; güvenlik doğrulaması kapatılmaz."); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SmtpCommandException or SmtpProtocolException or System.Net.Sockets.SocketException)
        { throw new EmailDeliveryException("SMTP_CONNECT_FAILED", "Mail sunucusuna bağlanılamadı; tekrar denenecek.", canRetry: true); }
    }
}
