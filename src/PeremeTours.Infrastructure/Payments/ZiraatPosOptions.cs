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

    public string ApplicationName { get; init; } = "PeremeTours";

    public string OrderPrefix { get; init; } = "PRM";
}
