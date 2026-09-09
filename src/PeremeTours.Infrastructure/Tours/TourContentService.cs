using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tours;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Tours;

internal sealed class TourContentService(
    PeremeToursDbContext dbContext,
    ITourCatalogService tourCatalogService,
    ITourImageStorage imageStorage
) : ITourContentService
{
    public async Task<IReadOnlyList<PublicTourItem>> ListPublicAsync(
        CancellationToken cancellationToken
    )
    {
        var catalog = await tourCatalogService.ListAsync(cancellationToken);
        var externalTourIds = catalog
            .Select(item => item.ExternalTourId)
            .ToArray();
        var contents = await dbContext.TourContents
            .AsNoTracking()
            .Where(content => externalTourIds.Contains(content.ExternalTourId))
            .ToDictionaryAsync(
                content => content.ExternalTourId,
                cancellationToken
            );

        return catalog
            .Select((item, index) => MapPublic(
                item,
                contents.GetValueOrDefault(item.ExternalTourId),
                index
            ))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.ExternalTourId)
            .ToList();
    }

    public async Task<IReadOnlyList<AdminTourContentItem>> ListAdminAsync(
        CancellationToken cancellationToken
    )
    {
        var catalog = await tourCatalogService.ListAsync(cancellationToken);
        var contents = await dbContext.TourContents
            .AsNoTracking()
            .ToDictionaryAsync(
                content => content.ExternalTourId,
                cancellationToken
            );

        return catalog
            .Select((item, index) => MapAdmin(
                item,
                contents.GetValueOrDefault(item.ExternalTourId),
                index
            ))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.ExternalTourId)
            .ToList();
    }

    public async Task<AdminTourContentItem?> UpdateAsync(
        int externalTourId,
        UpdateTourContentCommand command,
        CancellationToken cancellationToken
    )
    {
        var catalog = await tourCatalogService.ListAsync(cancellationToken);
        var catalogEntry = catalog.FirstOrDefault(
            item => item.ExternalTourId == externalTourId
        );
        if (catalogEntry is null)
        {
            return null;
        }

        var content = await GetOrCreateAsync(externalTourId, cancellationToken);
        content.TitleTr = Normalize(command.TitleTr);
        content.TitleEn = Normalize(command.TitleEn);
        content.DescriptionTr = Normalize(command.DescriptionTr);
        content.DescriptionEn = Normalize(command.DescriptionEn);
        content.BadgeTr = Normalize(command.BadgeTr);
        content.BadgeEn = Normalize(command.BadgeEn);
        content.SortOrder = command.SortOrder;
        content.IsVisible = command.IsVisible;
        content.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return MapAdmin(
            catalogEntry,
            content,
            IndexOf(catalog, externalTourId)
        );
    }

    public async Task<AdminTourContentItem?> UploadImageAsync(
        int externalTourId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken
    )
    {
        var catalog = await tourCatalogService.ListAsync(cancellationToken);
        var catalogEntry = catalog.FirstOrDefault(
            item => item.ExternalTourId == externalTourId
        );
        if (catalogEntry is null)
        {
            return null;
        }

        var tourContent = await GetOrCreateAsync(
            externalTourId,
            cancellationToken
        );
        var previousObjectKey = tourContent.ImageObjectKey;
        var objectKey = await imageStorage.SaveAsync(
            externalTourId,
            content,
            contentType,
            cancellationToken
        );
        try
        {
            tourContent.ImageObjectKey = objectKey;
            tourContent.ImageContentType = contentType;
            tourContent.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await imageStorage.DeleteAsync(objectKey, cancellationToken);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(previousObjectKey))
        {
            await imageStorage.DeleteAsync(previousObjectKey, cancellationToken);
        }

        return MapAdmin(
            catalogEntry,
            tourContent,
            IndexOf(catalog, externalTourId)
        );
    }

    public async Task<bool> DeleteImageAsync(
        int externalTourId,
        CancellationToken cancellationToken
    )
    {
        var content = await dbContext.TourContents.FindAsync(
            [externalTourId],
            cancellationToken
        );
        if (content?.ImageObjectKey is null)
        {
            return false;
        }

        var objectKey = content.ImageObjectKey;
        content.ImageObjectKey = null;
        content.ImageContentType = null;
        content.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await imageStorage.DeleteAsync(objectKey, cancellationToken);
        return true;
    }

    public async Task<TourImageFile?> GetImageAsync(
        int externalTourId,
        CancellationToken cancellationToken
    )
    {
        var content = await dbContext.TourContents
            .AsNoTracking()
            .Where(item => item.ExternalTourId == externalTourId)
            .Select(item => new
            {
                item.ImageObjectKey,
                item.ImageContentType,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (content?.ImageObjectKey is null)
        {
            return null;
        }

        return await imageStorage.GetAsync(
            content.ImageObjectKey,
            content.ImageContentType,
            cancellationToken
        );
    }

    private async Task<TourContent> GetOrCreateAsync(
        int externalTourId,
        CancellationToken cancellationToken
    )
    {
        var content = await dbContext.TourContents.FindAsync(
            [externalTourId],
            cancellationToken
        );
        if (content is not null)
        {
            return content;
        }

        var now = DateTimeOffset.UtcNow;
        content = new TourContent
        {
            ExternalTourId = externalTourId,
            IsVisible = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        dbContext.TourContents.Add(content);
        return content;
    }

    private static PublicTourItem? MapPublic(
        TourCatalogItem item,
        TourContent? content,
        int catalogIndex
    )
    {
        if (content is { IsVisible: false })
        {
            return null;
        }

        return new PublicTourItem(
            item.ExternalTourId,
            item.ExternalCategoryId,
            item.CategoryKey,
            item.CategoryName,
            item.Name,
            content?.TitleTr,
            content?.TitleEn,
            content?.DescriptionTr,
            content?.DescriptionEn,
            content?.BadgeTr,
            content?.BadgeEn,
            BuildImageUrl(item.ExternalTourId, content),
            content?.SortOrder ?? GetDefaultSortOrder(item.CategoryKey, catalogIndex)
        );
    }

    private static AdminTourContentItem MapAdmin(
        TourCatalogItem item,
        TourContent? content,
        int catalogIndex
    ) => new(
        item.ExternalTourId,
        item.ExternalCategoryId,
        item.CategoryKey,
        item.CategoryName,
        item.Name,
        content?.TitleTr,
        content?.TitleEn,
        content?.DescriptionTr,
        content?.DescriptionEn,
        content?.BadgeTr,
        content?.BadgeEn,
        BuildImageUrl(item.ExternalTourId, content),
        !string.IsNullOrWhiteSpace(content?.ImageObjectKey),
        content?.SortOrder ?? GetDefaultSortOrder(item.CategoryKey, catalogIndex),
        content?.IsVisible ?? true,
        content is not null,
        content?.UpdatedAtUtc
    );

    private static string? BuildImageUrl(
        int externalTourId,
        TourContent? content
    ) => string.IsNullOrWhiteSpace(content?.ImageObjectKey)
        ? null
        : $"/api/v1/tours/{externalTourId}/image?v={content.UpdatedAtUtc.ToUnixTimeSeconds()}";

    private static int GetDefaultSortOrder(string categoryKey, int index) =>
        categoryKey switch
        {
            TourCategoryKeys.TurkishNight => 100 + index,
            TourCategoryKeys.Sunset => 200 + index,
            TourCategoryKeys.Daytime => 300 + index,
            TourCategoryKeys.Bosphorus => 400 + index,
            _ => 900 + index,
        };

    private static int IndexOf(
        IReadOnlyList<TourCatalogItem> catalog,
        int externalTourId
    )
    {
        for (var index = 0; index < catalog.Count; index++)
        {
            if (catalog[index].ExternalTourId == externalTourId)
            {
                return index;
            }
        }

        return catalog.Count;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
