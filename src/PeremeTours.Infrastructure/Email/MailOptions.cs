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
    public string WebsiteUrl { get; set; } = "https://pereme.com.tr";
    // Defaults live here, not in appsettings: the configuration binder appends configured array items to these.
    public string[] TurkishNightRecipients { get; set; } = ["skurt@pereme.com.tr", "eakyuz@pereme.com.tr", "reservation@pereme.com.tr"];
    public string[] BosphorusRecipients { get; set; } = ["mcanerdinc@pereme.com.tr"];
    public string[] SunsetDaytimeRecipients { get; set; } = ["omacit@pereme.com.tr", "dpardun@pereme.com.tr"];
    public string[] ContactRecipients { get; set; } = ["eyilmaz@denturavrasya.com", "uakseloglu@denturavrasya.com"];
}
