namespace PeremeTours.Infrastructure.Tours;

public sealed class TourImageStorageOptions
{
    public const string SectionName = "TourImages";

    public string BucketName { get; init; } = string.Empty;

    public string Region { get; init; } = "eu-central-1";

    public string Prefix { get; init; } = "tour-content";
}
