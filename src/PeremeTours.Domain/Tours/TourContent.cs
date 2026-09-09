namespace PeremeTours.Domain.Tours;

public sealed class TourContent
{
    public int ExternalTourId { get; set; }

    public string? TitleTr { get; set; }

    public string? TitleEn { get; set; }

    public string? DescriptionTr { get; set; }

    public string? DescriptionEn { get; set; }

    public string? BadgeTr { get; set; }

    public string? BadgeEn { get; set; }

    public string? ImageObjectKey { get; set; }

    public string? ImageContentType { get; set; }

    public int? SortOrder { get; set; }

    public bool IsVisible { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
