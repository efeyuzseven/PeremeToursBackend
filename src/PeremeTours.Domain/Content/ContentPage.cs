namespace PeremeTours.Domain.Content;

// A blog post or a standalone site page. Turkish is required; English is optional
// and only published when its title, slug and body are present.
public sealed class ContentPage
{
    public Guid Id { get; set; }

    public required string Kind { get; set; }

    public bool IsPublished { get; set; }

    // Visible from this moment on; lets a post be scheduled.
    public DateTimeOffset? PublishedAtUtc { get; set; }

    public string? CoverImageUrl { get; set; }

    public bool ShowInFooter { get; set; }

    public int SortOrder { get; set; }

    public required string TitleTr { get; set; }

    public required string SlugTr { get; set; }

    public string? SummaryTr { get; set; }

    public required string BodyTr { get; set; }

    public string? MetaTitleTr { get; set; }

    public string? MetaDescriptionTr { get; set; }

    public string? TitleEn { get; set; }

    public string? SlugEn { get; set; }

    public string? SummaryEn { get; set; }

    public string? BodyEn { get; set; }

    public string? MetaTitleEn { get; set; }

    public string? MetaDescriptionEn { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
