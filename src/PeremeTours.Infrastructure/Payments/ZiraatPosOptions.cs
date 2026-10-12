namespace PeremeTours.Infrastructure.Payments;

public sealed class ZiraatPosOptions
{
    public const string SectionName = "Payments:Ziraat";

    public bool Enabled { get; init; }

    public string GatewayUrl { get; init; } = string.Empty;

    public string ApiUrl { get; init; } = string.Empty;

    public string MerchantId { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string StoreKey { get; init; } = string.Empty;

    public string ApiUser { get; init; } = string.Empty;

    public string ApiPassword { get; init; } = string.Empty;

    public string CallbackUrl { get; init; } = string.Empty;

    public string FrontendOrigin { get; init; } = string.Empty;
    public string[] AllowedFrontendOrigins { get; init; } = [];

    // Explicit HTTPS origin allowlist; never reflect Origin/Referer or use '*'.
    public IReadOnlyList<string> GetFrontendOrigins() => AllowedFrontendOrigins.Prepend(FrontendOrigin)
        .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment) && !value.Contains('*'))
        .Select(value => new Uri(value).GetLeftPart(UriPartial.Authority))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public string ApplicationName { get; init; } = "PeremeTours";

    public string OrderPrefix { get; init; } = "PRM";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(MerchantId)
        && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(StoreKey)
        && !string.IsNullOrWhiteSpace(ApiUser) && !string.IsNullOrWhiteSpace(ApiPassword)
        && IsHttps(GatewayUrl) && IsHttps(ApiUrl) && IsHttps(CallbackUrl) && IsHttps(FrontendOrigin);

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;
}
