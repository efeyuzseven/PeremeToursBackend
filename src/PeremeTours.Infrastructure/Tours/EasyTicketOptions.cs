namespace PeremeTours.Infrastructure.Tours;

public sealed class EasyTicketOptions
{
    public const string SectionName = "EasyTicket";

    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public int CacheMinutes { get; init; } = 5;
}
