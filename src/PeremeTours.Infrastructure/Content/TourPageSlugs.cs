using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

// The readable address of a tour detail page (pereme.com.tr/tur/{slug}) is stored as "slug" in the page's saved
// document. Only tour detail pages ("tour-{id}") have one; a page without a slug is reached by its tour number.
internal static class TourPageSlugs
{
    private const string Property = "\"slug\":\"";

    // Tour number -> slug, for every tour detail page that has one.
    public static async Task<Dictionary<int, string>> LoadAsync(PeremeToursDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.TourPages.AsNoTracking()
            .Where(page => page.Key.StartsWith("tour-") && page.ContentJson.Contains(Property))
            .Select(page => new { page.Key, page.ContentJson })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<int, string>();
        foreach (var row in rows)
        {
            if (int.TryParse(row.Key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && Read(row.ContentJson) is { } slug)
                result[id] = slug;
        }
        return result;
    }

    // Key of the tour detail page that uses this slug, or null.
    public static async Task<string?> FindKeyAsync(PeremeToursDbContext db, string slug, CancellationToken cancellationToken)
    {
        var needle = $"{Property}{slug}\"";
        var rows = await db.TourPages.AsNoTracking()
            .Where(page => page.Key.StartsWith("tour-") && page.ContentJson.Contains(needle))
            .Select(page => new { page.Key, page.ContentJson })
            .ToListAsync(cancellationToken);
        return rows.FirstOrDefault(row => Read(row.ContentJson) == slug)?.Key;
    }

    private static string? Read(string contentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("slug", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } slug ? slug : null;
        }
        catch (JsonException) { return null; }
    }
}
