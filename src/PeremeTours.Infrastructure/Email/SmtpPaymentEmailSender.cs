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

internal interface IReservationNotificationSender
{
    Task SendAsync(ReservationNotification notification, CancellationToken cancellationToken);
}

internal sealed class EmailDeliveryException(string code, string message, bool canRetry = false, bool isAmbiguous = false)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool CanRetry { get; } = canRetry;
    public bool IsAmbiguous { get; } = isAmbiguous;
}

internal sealed class SmtpPaymentEmailSender(IOptions<MailOptions> options) : IPaymentEmailSender, IReservationNotificationSender
{
    private readonly MailOptions _options = options.Value;

    // No email is sent. Authentication is optional and never writes a message.
    public async Task CheckConnectionAsync(CancellationToken cancellationToken, bool authenticate = false)
    {
        using var smtp = new SmtpClient { Timeout = 40_000 };
        await ConnectAsync(smtp, cancellationToken);
        if (authenticate) await AuthenticateAsync(smtp, cancellationToken);
        await smtp.DisconnectAsync(true, cancellationToken);
    }

    public async Task SendAsync(TourTicket ticket, CancellationToken cancellationToken)
    {
        await DeliverAsync(CreateCustomerMessage(ticket), cancellationToken);
    }

    public async Task SendAsync(ReservationNotification notification, CancellationToken cancellationToken)
    {
        await DeliverAsync(CreateReservationMessage(notification), cancellationToken);
    }

    internal MimeMessage CreateCustomerMessage(TourTicket ticket)
    {
        var sender = GetSender();
        if (!MailboxAddress.TryParse(ticket.CustomerEmail, out var recipient))
            throw new EmailDeliveryException("EMAIL_ADDRESS_INVALID", "Rezervasyon iletişim e-posta adresi geçersiz.");
        return CreateMessage(ticket, recipient, sender, PaymentEmailTemplate.Render(ticket, _options), $"pereme-payment-{ticket.Id:N}");
    }

    internal MimeMessage CreateReservationMessage(ReservationNotification notification)
    {
        var sender = GetSender();
        if (!MailboxAddress.TryParse(notification.RecipientEmail, out var recipient))
            throw new EmailDeliveryException("EMAIL_ADDRESS_INVALID", "İç rezervasyon bildirimi alıcı adresi geçersiz.");
        return CreateMessage(notification.Ticket, recipient, sender, ReservationNotificationTemplate.Render(notification.Ticket, _options),
            $"pereme-reservation-{notification.Id:N}");
    }

    private MailboxAddress GetSender()
    {
        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password)
            || !MailboxAddress.TryParse(_options.SenderEmail, out var sender))
            throw new EmailDeliveryException("SMTP_CONFIG_MISSING", "Mail sunucusu ayarları eksik; gönderim başlatılmadı.");
        return sender;
    }

    private MimeMessage CreateMessage(TourTicket ticket, MailboxAddress recipient, MailboxAddress sender, RenderedPaymentEmail content, string messageId)
    {
        var message = new MimeMessage
        {
            Subject = content.Subject,
            MessageId = $"{messageId}@{sender.Domain}",
            Date = ticket.PaidAtUtc ?? ticket.CreatedAtUtc,
            Body = new BodyBuilder { HtmlBody = content.Html, TextBody = content.Text }.ToMessageBody(),
        };
        message.From.Add(new MailboxAddress(_options.SenderName, sender.Address));
        message.To.Add(recipient);
        return message;
    }

    private async Task DeliverAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var smtp = new SmtpClient { Timeout = 40_000 };
        await ConnectAsync(smtp, cancellationToken);
        await AuthenticateAsync(smtp, cancellationToken);
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

    internal static SecureSocketOptions ResolveSecurity(MailOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Server) || options.Port is < 1 or > 65535
            || !Enum.IsDefined(options.SecurityMode) || (options.SecurityMode == MailSecurityMode.None && options.Port == 465))
            throw new EmailDeliveryException("SMTP_CONFIG_MISSING", "Mail sunucusu, portu veya bağlantı güvenliği ayarı geçersiz.");
        return options.SecurityMode switch
        {
            MailSecurityMode.None => SecureSocketOptions.None,
            MailSecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };
    }

    private async Task AuthenticateAsync(SmtpClient smtp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
            throw new EmailDeliveryException("SMTP_CONFIG_MISSING", "Mail sunucusu kullanıcı ayarları eksik.");
        try { await smtp.AuthenticateAsync(_options.Username, _options.Password, cancellationToken); }
        catch (AuthenticationException)
        { throw new EmailDeliveryException("SMTP_AUTH_FAILED", "Mail sunucusu kullanıcı doğrulamasını reddetti. SMTP hesabı kontrol edilmeli."); }
        catch (Exception exception) when (exception is SmtpCommandException or IOException or OperationCanceledException or SmtpProtocolException)
        { throw new EmailDeliveryException("SMTP_CONNECT_FAILED", "Mail sunucusuyla bağlantı tamamlanamadı; tekrar denenecek.", canRetry: true); }
    }

    private async Task ConnectAsync(SmtpClient smtp, CancellationToken cancellationToken)
    {
        var security = ResolveSecurity(_options);
        try
        {
            // Explicit None is provider-specific. TLS modes retain certificate validation and never fall back.
            await smtp.ConnectAsync(_options.Server, _options.Port, security, cancellationToken);
        }
        catch (Exception exception) when (exception is SslHandshakeException or System.Security.Authentication.AuthenticationException or NotSupportedException)
        { throw new EmailDeliveryException("SMTP_TLS_FAILED", "Mail sunucusunun güvenli TLS bağlantısı doğrulanamadı. Sunucu sertifikası kontrol edilmeli; güvenlik doğrulaması kapatılmaz."); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or SmtpCommandException or SmtpProtocolException or System.Net.Sockets.SocketException)
        { throw new EmailDeliveryException("SMTP_CONNECT_FAILED", "Mail sunucusuna bağlanılamadı; tekrar denenecek.", canRetry: true); }
    }
}
