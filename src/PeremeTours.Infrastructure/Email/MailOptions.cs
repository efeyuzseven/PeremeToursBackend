namespace PeremeTours.Infrastructure.Email;

public sealed class MailOptions
{
    public const string SectionName = "MailSettings";
    public bool Enabled { get; set; }
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = "Dentur Pereme";
    public string WebsiteUrl { get; set; } = "https://d2bmjk2h6qp4lz.cloudfront.net";
}
