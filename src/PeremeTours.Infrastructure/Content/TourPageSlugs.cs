using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

// The readable address of a tour detail page (pereme.com.tr/tur/{slug}) is stored as "slug" in the page's saved
// document. Only tour detail pages ("tour-{id}") have one; a page without a slug is reached by its tour number.
//
// The documents are read and compared here, never searched in SQL: the column is jsonb in PostgreSQL, where text
// search on it fails and the stored text is re-formatted. There is one row per tour, so reading them all is cheap.
internal static class TourPageSlugs
{
    // Tour number -> slug, for every tour detail page that has one.
    public static async Task<Dictionary<int, string>> LoadAsync(PeremeToursDbContext db, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        foreach (var (key, slug) in await ReadAllAsync(db, cancellationToken))
        {
            if (int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) result[id] = slug;
        }
        return result;
    }

    // Key of the tour detail page that uses this slug, or null.
    public static async Task<string?> FindKeyAsync(PeremeToursDbContext db, string slug, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in await ReadAllAsync(db, cancellationToken))
        {
            if (value == slug) return key;
        }
        return null;
    }

    private static async Task<List<(string Key, string Slug)>> ReadAllAsync(PeremeToursDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.TourPages.AsNoTracking()
            .Where(page => page.Key.StartsWith("tour-"))
            .Select(page => new { page.Key, page.ContentJson })
            .ToListAsync(cancellationToken);
        var result = new List<(string, string)>();
        foreach (var row in rows)
        {
            if (Read(row.ContentJson) is { } slug) result.Add((row.Key, slug));
        }
        return result;
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
