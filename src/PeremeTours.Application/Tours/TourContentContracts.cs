namespace PeremeTours.Application.Tours;

public sealed record PublicTourItem(
    int ExternalTourId,
    int ExternalCategoryId,
    string CategoryKey,
    string CategoryName,
    string Name,
    string? TitleTr,
    string? TitleEn,
    string? DescriptionTr,
    string? DescriptionEn,
    string? BadgeTr,
    string? BadgeEn,
    string? ImageUrl,
    int SortOrder,
    // Extra card images after ImageUrl; with at least one, the site lets visitors flip through them on the tour card.
    IReadOnlyList<string>? GalleryImageUrls = null
);

public sealed record AdminTourContentItem(
    int ExternalTourId,
    int ExternalCategoryId,
    string CategoryKey,
    string CategoryName,
    string SourceName,
    string? TitleTr,
    string? TitleEn,
    string? DescriptionTr,
    string? DescriptionEn,
    string? BadgeTr,
    string? BadgeEn,
    string? ImageUrl,
    bool HasCustomImage,
    int SortOrder,
    bool IsVisible,
    bool IsCustomized,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<string>? GalleryImageUrls = null
);

public sealed record UpdateTourContentCommand(
    string? TitleTr,
    string? TitleEn,
    string? DescriptionTr,
    string? DescriptionEn,
    string? BadgeTr,
    string? BadgeEn,
    int SortOrder,
    bool IsVisible,
    // Null keeps the stored gallery as it is (older clients do not send it); an empty list clears it.
    IReadOnlyList<string>? GalleryImageUrls = null
);

public sealed record TourImageFile(byte[] Content, string ContentType);

public interface ITourContentService
{
    Task<IReadOnlyList<PublicTourItem>> ListPublicAsync(
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<AdminTourContentItem>> ListAdminAsync(
        CancellationToken cancellationToken
    );

    Task<AdminTourContentItem?> UpdateAsync(
        int externalTourId,
        UpdateTourContentCommand command,
        CancellationToken cancellationToken
    );

    Task<AdminTourContentItem?> UploadImageAsync(
        int externalTourId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken
    );

    Task<bool> DeleteImageAsync(
        int externalTourId,
        CancellationToken cancellationToken
    );

    Task<TourImageFile?> GetImageAsync(
        int externalTourId,
        CancellationToken cancellationToken
    );
}
