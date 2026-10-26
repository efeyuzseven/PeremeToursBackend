using System.Globalization;
using System.Text;

namespace PeremeTours.Application.Content;

public static class ContentPageKinds
{
    public const string Blog = "blog";
    public const string Page = "page";

    public static bool IsValid(string? kind) => kind is Blog or Page;
}

// Full record for the admin editor.
public sealed record ContentPageSummary(
    Guid Id,
    string Kind,
    bool IsPublished,
    DateTimeOffset? PublishedAtUtc,
    string? CoverImageUrl,
    bool ShowInFooter,
    int SortOrder,
    ContentPageText Tr,
    ContentPageText? En,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

public sealed record ContentPageText(
    string Title,
    string Slug,
    string? Summary,
    string Body,
    string? MetaTitle,
    string? MetaDescription
);

public sealed record SaveContentPageCommand(
    string Kind,
    bool IsPublished,
    DateTimeOffset? PublishedAtUtc,
    string? CoverImageUrl,
    bool ShowInFooter,
    int SortOrder,
    SaveContentPageText Tr,
    SaveContentPageText? En
);

// Slug may be empty: it is then derived from the title.
public sealed record SaveContentPageText(
    string Title,
    string? Slug,
    string? Summary,
    string Body,
    string? MetaTitle,
    string? MetaDescription
);

// Public view in one language; SlugTr/SlugEn let the site switch language on the same item.
public sealed record PublicContentPage(
    string Kind,
    string Language,
    string Title,
    string Slug,
    string? Summary,
    string Body,
    string? MetaTitle,
    string? MetaDescription,
    string? CoverImageUrl,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string SlugTr,
    string? SlugEn
);

public sealed record PublicContentPageListItem(
    string Title,
    string Slug,
    string? Summary,
    string? CoverImageUrl,
    DateTimeOffset? PublishedAtUtc
);

public sealed record PublicContentPageList(
    IReadOnlyList<PublicContentPageListItem> Items,
    int Total,
    int Page,
    int PageSize
);

public sealed record ContentPageLink(string Title, string Slug);

public sealed record ContentPageSitemapEntry(string Kind, string SlugTr, string? SlugEn, DateTimeOffset UpdatedAtUtc);

public sealed class ContentPageSlugConflictException(string language) : Exception
{
    public string Language { get; } = language;
}

public sealed class ContentPageReservedSlugException(string slug) : Exception
{
    public string Slug { get; } = slug;
}

public interface IContentPageService
{
    Task<PublicContentPageList> ListPublishedAsync(string kind, string language, int page, int pageSize, CancellationToken cancellationToken);

    Task<PublicContentPage?> GetPublishedAsync(string kind, string language, string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentPageLink>> ListFooterPagesAsync(string language, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentPageSitemapEntry>> ListSitemapEntriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentPageSummary>> ListAdminAsync(string kind, CancellationToken cancellationToken);

    Task<ContentPageSummary> CreateAsync(SaveContentPageCommand command, CancellationToken cancellationToken);

    Task<ContentPageSummary?> UpdateAsync(Guid id, SaveContentPageCommand command, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public static class ContentSlug
{
    // Top-level paths the site already uses; a standalone page cannot take them.
    public static readonly IReadOnlySet<string> ReservedPageSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "turlar", "tur", "blog", "en", "admin", "api", "assets", "login", "giris", "sifremi-unuttum", "sifre-sifirla",
        "hesabim", "account", "iletisim", "contact", "sikca-sorulan-sorular", "faq", "kvkk-aydinlatma-metni", "kvkk",
        "mesafeli-satis-sozlesmesi", "distance-sales-agreement", "odeme", "payment", "sitemap", "robots",
    };

    // Lowercase ASCII words joined by single hyphens; Turkish letters are transliterated (ı→i, ş→s, ğ→g …).
    public static string Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"))
            .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');
        var builder = new StringBuilder(text.Length);
        var pendingHyphen = false;
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            // Accents and apostrophes ("Boğaz'da" → "bogazda") are dropped rather than turned into hyphens.
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark || character is '\'' or '’' or '‘' or '`') continue;
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && builder.Length > 0) builder.Append('-');
                builder.Append(character);
                pendingHyphen = false;
            }
            else pendingHyphen = true;
        }
        var slug = builder.ToString();
        return slug.Length <= 120 ? slug : slug[..120].TrimEnd('-');
    }
}
