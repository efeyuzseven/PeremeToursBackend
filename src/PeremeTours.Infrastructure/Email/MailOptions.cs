namespace PeremeTours.Infrastructure.Email;

public enum MailSecurityMode { StartTls, SslOnConnect, None }

public sealed class MailOptions
{
    public const string SectionName = "MailSettings";
    public bool Enabled { get; set; }
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    // Plaintext is opt-in only; a TLS failure must never downgrade the connection.
    public MailSecurityMode SecurityMode { get; set; } = MailSecurityMode.StartTls;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = "Dentur Pereme";
    public string WebsiteUrl { get; set; } = "https://d2bmjk2h6qp4lz.cloudfront.net";
    public string ReservationRecipient { get; set; } = "reservation@pereme.com.tr";
    public string SunsetDaytimeRecipient { get; set; } = "omacit@pereme.com.tr";
}
