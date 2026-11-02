namespace PeremeTours.Application.Content;

public sealed record GoogleReview(
    string AuthorName,
    string? AuthorUrl,
    string? AuthorPhotoUrl,
    int Rating,
    string Text,
    // True when Google translated the review into the requested language.
    bool Translated,
    string? RelativeTime,
    DateTimeOffset? PublishedAt
);

public sealed record GoogleReviewSummary(
    decimal Rating,
    int TotalCount,
    string MapsUrl,
    IReadOnlyList<GoogleReview> Reviews
);

public interface IGoogleReviewService
{
    // Null while reviews are not configured or Google cannot be reached; the site then hides the section.
    Task<GoogleReviewSummary?> GetAsync(string language, CancellationToken cancellationToken);
}
