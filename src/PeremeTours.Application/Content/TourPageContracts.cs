using System.ComponentModel.DataAnnotations;
using PeremeTours.Application.Tours;

namespace PeremeTours.Application.Content;

public sealed record TourPageDocument(
    [Required, RegularExpression("^(cover|split)$")] string HeroLayout,
    [StringLength(2000)] string? HeroImageUrl,
    [Required] TourPageLanguageContent Tr,
    [Required] TourPageLanguageContent En,
    [Required, MaxLength(8)] IReadOnlyList<TourPageSection> Sections
);

public sealed record TourPageLanguageContent(
    [Required, StringLength(100)] string Eyebrow,
    [Required, StringLength(200)] string Title,
    [Required, StringLength(3000)] string Intro,
    [Required, StringLength(80)] string BookingLabel,
    [Required, StringLength(200)] string ToursHeading,
    [Required, StringLength(320)] string SeoDescription
);

public sealed record TourPageSection(
    [Required, StringLength(80), RegularExpression("^[a-zA-Z0-9_-]+$")] string Id,
    [Required, RegularExpression("^(text|highlights|gallery)$")] string Type,
    [Required, RegularExpression("^(white|blue)$")] string Background,
    bool IsVisible,
    [Required, MaxLength(6)] IReadOnlyList<string> ImageUrls,
    [Required] TourPageSectionLanguageContent Tr,
    [Required] TourPageSectionLanguageContent En
);

public sealed record TourPageSectionLanguageContent(
    [Required, StringLength(200)] string Title,
    [StringLength(6000)] string? Body,
    [Required, MaxLength(12)] IReadOnlyList<string> Items
);

public sealed record TourPageItem(string Key, string CategoryKey, int? ExternalTourId,
    string SourceName, TourPageDocument Document, bool IsCustomized, DateTimeOffset? UpdatedAtUtc);

public sealed record TourPageImageResult(string Url);

public sealed class TourPageImageLimitException : Exception;

public interface ITourPageService
{
    Task<IReadOnlyList<TourPageItem>> ListAdminAsync(CancellationToken cancellationToken);
    Task<TourPageItem?> GetAsync(string key, bool includeHidden, CancellationToken cancellationToken);
    Task<TourPageItem?> UpdateAsync(string key, TourPageDocument document, CancellationToken cancellationToken);
    Task<TourPageImageResult?> UploadImageAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);
    Task<TourImageFile?> GetImageAsync(string key, Guid imageId, CancellationToken cancellationToken);
}
