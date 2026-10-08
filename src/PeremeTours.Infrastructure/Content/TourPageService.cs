using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.Infrastructure.Content;

internal sealed class TourPageService(PeremeToursDbContext db, ITourContentService tours,
    ITourImageStorage imageStorage, ITourPageImageStorage pageImageStorage) : ITourPageService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TourPageItem>> ListAdminAsync(CancellationToken cancellationToken)
    {
        var catalog = await tours.ListAdminAsync(cancellationToken);
        var saved = await db.TourPages.AsNoTracking().ToDictionaryAsync(page => page.Key, cancellationToken);
        var result = TourPageDefaults.Categories.Select(category => Map(category, category, null, category,
            TourPageDefaults.Create(category), saved.GetValueOrDefault(category))).ToList();
        result.AddRange(catalog.Select(tour => Map(TourKey(tour.ExternalTourId), tour.CategoryKey, tour.ExternalTourId,
            tour.SourceName, TourPageDefaults.Create(tour.CategoryKey, tour.TitleTr ?? tour.SourceName,
                tour.TitleEn ?? tour.SourceName, tour.DescriptionTr, tour.DescriptionEn), saved.GetValueOrDefault(TourKey(tour.ExternalTourId)))));
        return result;
    }

    public async Task<TourPageItem?> GetAsync(string key, bool includeHidden, CancellationToken cancellationToken)
    {
        if (TourPageDefaults.Categories.Contains(key, StringComparer.Ordinal))
        {
            var page = await db.TourPages.AsNoTracking().SingleOrDefaultAsync(page => page.Key == key, cancellationToken);
            return Map(key, key, null, key, TourPageDefaults.Create(key), page);
        }
        if (!TryTourId(key, out var id)) return null;
        if (includeHidden) return (await ListAdminAsync(cancellationToken)).FirstOrDefault(item => item.Key == key);
        var tour = (await tours.ListPublicAsync(cancellationToken)).FirstOrDefault(tour => tour.ExternalTourId == id);
        if (tour is null) return null;
        var saved = await db.TourPages.AsNoTracking().SingleOrDefaultAsync(page => page.Key == key, cancellationToken);
        return Map(key, tour.CategoryKey, id, tour.Name, TourPageDefaults.Create(tour.CategoryKey,
            tour.TitleTr ?? tour.Name, tour.TitleEn ?? tour.Name, tour.DescriptionTr, tour.DescriptionEn), saved);
    }

    public async Task<TourPageItem?> UpdateAsync(string key, TourPageDocument document, CancellationToken cancellationToken)
    {
        if (await GetAsync(key, true, cancellationToken) is not { } item) return null;
        var page = await GetOrCreateAsync(item, cancellationToken);
        page.ContentJson = JsonSerializer.Serialize(document, JsonOptions);
        page.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return item with { Document = document, IsCustomized = true, UpdatedAtUtc = page.UpdatedAtUtc };
    }

    public async Task<TourPageImageResult?> UploadImageAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        if (await GetAsync(key, true, cancellationToken) is not { } item) return null;
        if (await db.TourPageImages.CountAsync(image => image.PageKey == key, cancellationToken) >= 40)
            throw new TourPageImageLimitException();
        await GetOrCreateAsync(item, cancellationToken);
        var objectKey = await pageImageStorage.SavePageImageAsync(key, content, contentType, cancellationToken);
        var image = new TourPageImage { Id = Guid.NewGuid(), PageKey = key, ObjectKey = objectKey, ContentType = contentType };
        db.TourPageImages.Add(image);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { await imageStorage.DeleteAsync(objectKey, CancellationToken.None); throw; }
        return new TourPageImageResult($"/api/v1/tour-pages/{key}/images/{image.Id}");
    }

    public async Task<TourImageFile?> GetImageAsync(string key, Guid imageId, CancellationToken cancellationToken)
    {
        var image = await db.TourPageImages.AsNoTracking().SingleOrDefaultAsync(image => image.Id == imageId && image.PageKey == key, cancellationToken);
        return image is null ? null : await imageStorage.GetAsync(image.ObjectKey, image.ContentType, cancellationToken);
    }

    private async Task<TourPage> GetOrCreateAsync(TourPageItem item, CancellationToken cancellationToken)
    {
        if (await db.TourPages.FindAsync([item.Key], cancellationToken) is { } page) return page;
        page = new TourPage { Key = item.Key, ContentJson = JsonSerializer.Serialize(item.Document, JsonOptions), UpdatedAtUtc = DateTimeOffset.UtcNow };
        db.TourPages.Add(page);
        return page;
    }

    private static TourPageItem Map(string key, string category, int? tourId, string name, TourPageDocument defaults, TourPage? page)
        => new(key, category, tourId, name, page is null ? defaults : JsonSerializer.Deserialize<TourPageDocument>(page.ContentJson, JsonOptions) ?? defaults,
            page is not null, page?.UpdatedAtUtc);

    private static string TourKey(int id) => $"tour-{id.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryTourId(string key, out int id)
    {
        id = 0;
        return key.StartsWith("tour-", StringComparison.Ordinal) && int.TryParse(key.AsSpan(5), NumberStyles.None,
            CultureInfo.InvariantCulture, out id) && id > 0 && key == TourKey(id);
    }
}
