using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

internal sealed class ContentPageService(PeremeToursDbContext db, TimeProvider clock) : IContentPageService
{
    public async Task<PublicContentPageList> ListPublishedAsync(string kind, string language, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Published(kind, language);
        var total = await query.CountAsync(cancellationToken);
        var ordered = kind == ContentPageKinds.Page
            ? query.OrderBy(item => item.SortOrder).ThenBy(item => item.TitleTr)
            : query.OrderByDescending(item => item.PublishedAtUtc ?? item.CreatedAtUtc).ThenByDescending(item => item.CreatedAtUtc);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(item => language == "en"
            ? new PublicContentPageListItem(item.TitleEn!, item.SlugEn!, item.SummaryEn, item.CoverImageUrl, item.PublishedAtUtc ?? item.CreatedAtUtc)
            : new PublicContentPageListItem(item.TitleTr, item.SlugTr, item.SummaryTr, item.CoverImageUrl, item.PublishedAtUtc ?? item.CreatedAtUtc)).ToList(),
            total, page, pageSize);
    }

    public async Task<PublicContentPage?> GetPublishedAsync(string kind, string language, string slug, CancellationToken cancellationToken)
    {
        var normalized = ContentSlug.Create(slug);
        if (normalized.Length == 0) return null;
        var item = await Published(kind, language)
            .SingleOrDefaultAsync(entry => language == "en" ? entry.SlugEn == normalized : entry.SlugTr == normalized, cancellationToken);
        if (item is null) return null;
        var english = language == "en";
        return new(item.Kind, english ? "en" : "tr",
            english ? item.TitleEn! : item.TitleTr, english ? item.SlugEn! : item.SlugTr,
            english ? item.SummaryEn : item.SummaryTr, english ? item.BodyEn! : item.BodyTr,
            english ? item.MetaTitleEn : item.MetaTitleTr, english ? item.MetaDescriptionEn : item.MetaDescriptionTr,
            item.CoverImageUrl, item.PublishedAtUtc ?? item.CreatedAtUtc, item.UpdatedAtUtc, item.SlugTr, HasEnglish(item) ? item.SlugEn : null);
    }

    public async Task<IReadOnlyList<ContentPageLink>> ListFooterPagesAsync(string language, CancellationToken cancellationToken)
    {
        var rows = await Published(ContentPageKinds.Page, language).Where(item => item.ShowInFooter)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.TitleTr).ToListAsync(cancellationToken);
        return rows.Select(item => language == "en" ? new ContentPageLink(item.TitleEn!, item.SlugEn!) : new ContentPageLink(item.TitleTr, item.SlugTr)).ToList();
    }

    public async Task<IReadOnlyList<ContentPageSitemapEntry>> ListSitemapEntriesAsync(CancellationToken cancellationToken)
    {
        var rows = await Published(null, "tr").ToListAsync(cancellationToken);
        return rows.Select(item => new ContentPageSitemapEntry(item.Kind, item.SlugTr, HasEnglish(item) ? item.SlugEn : null, item.UpdatedAtUtc)).ToList();
    }

    public async Task<IReadOnlyList<ContentPageSummary>> ListAdminAsync(string kind, CancellationToken cancellationToken)
    {
        var rows = await db.ContentPages.AsNoTracking().Where(item => item.Kind == kind)
            .OrderBy(item => kind == ContentPageKinds.Page ? item.SortOrder : 0)
            .ThenByDescending(item => item.PublishedAtUtc ?? item.CreatedAtUtc).ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task<ContentPageSummary> CreateAsync(SaveContentPageCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var page = new ContentPage { Id = Guid.NewGuid(), Kind = command.Kind, TitleTr = "", SlugTr = "", BodyTr = "", CreatedAtUtc = now, UpdatedAtUtc = now };
        await ApplyAsync(page, command, cancellationToken);
        db.ContentPages.Add(page);
        await db.SaveChangesAsync(cancellationToken);
        return Map(page);
    }

    public async Task<ContentPageSummary?> UpdateAsync(Guid id, SaveContentPageCommand command, CancellationToken cancellationToken)
    {
        var page = await db.ContentPages.FindAsync([id], cancellationToken);
        if (page is null || page.Kind != command.Kind) return null;
        await ApplyAsync(page, command, cancellationToken);
        page.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Map(page);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var page = await db.ContentPages.FindAsync([id], cancellationToken);
        if (page is null) return false;
        db.ContentPages.Remove(page);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ApplyAsync(ContentPage page, SaveContentPageCommand command, CancellationToken cancellationToken)
    {
        var slugTr = await UniqueSlugAsync(page, command.Kind, "tr", command.Tr.Slug, command.Tr.Title, cancellationToken);
        var en = HasText(command.En?.Title) ? command.En : null;
        var slugEn = en is null ? null : await UniqueSlugAsync(page, command.Kind, "en", en.Slug, en.Title, cancellationToken);
        page.IsPublished = command.IsPublished;
        // Publishing without a date publishes now and keeps that date on later edits.
        page.PublishedAtUtc = command.PublishedAtUtc ?? page.PublishedAtUtc ?? (command.IsPublished ? clock.GetUtcNow() : null);
        page.CoverImageUrl = Clean(command.CoverImageUrl);
        page.ShowInFooter = command.Kind == ContentPageKinds.Page && command.ShowInFooter;
        page.SortOrder = command.SortOrder;
        page.TitleTr = command.Tr.Title.Trim();
        page.SlugTr = slugTr;
        page.SummaryTr = Clean(command.Tr.Summary);
        page.BodyTr = command.Tr.Body.Trim();
        page.MetaTitleTr = Clean(command.Tr.MetaTitle);
        page.MetaDescriptionTr = Clean(command.Tr.MetaDescription);
        page.TitleEn = Clean(en?.Title);
        page.SlugEn = slugEn;
        page.SummaryEn = Clean(en?.Summary);
        page.BodyEn = Clean(en?.Body);
        page.MetaTitleEn = Clean(en?.MetaTitle);
        page.MetaDescriptionEn = Clean(en?.MetaDescription);
    }

    private async Task<string> UniqueSlugAsync(ContentPage page, string kind, string language, string? requested, string title, CancellationToken cancellationToken)
    {
        var slug = ContentSlug.Create(HasText(requested) ? requested : title);
        if (slug.Length == 0) slug = $"{(kind == ContentPageKinds.Blog ? "yazi" : "sayfa")}-{page.Id.ToString("N")[..8]}";
        if (kind == ContentPageKinds.Page && ContentSlug.ReservedPageSlugs.Contains(slug)) throw new ContentPageReservedSlugException(slug);
        var taken = await db.ContentPages.AnyAsync(item => item.Id != page.Id && item.Kind == kind
            && (language == "en" ? item.SlugEn == slug : item.SlugTr == slug), cancellationToken);
        if (taken) throw new ContentPageSlugConflictException(language);
        return slug;
    }

    // Visible items; for English only those with a complete English version.
    private IQueryable<ContentPage> Published(string? kind, string language)
    {
        var now = clock.GetUtcNow();
        var query = db.ContentPages.AsNoTracking().Where(item => item.IsPublished && (item.PublishedAtUtc == null || item.PublishedAtUtc <= now));
        if (kind is not null) query = query.Where(item => item.Kind == kind);
        return language == "en" ? query.Where(item => item.TitleEn != null && item.SlugEn != null && item.BodyEn != null) : query;
    }

    private static bool HasEnglish(ContentPage item) => item.TitleEn is not null && item.SlugEn is not null && item.BodyEn is not null;

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string? Clean(string? value) => HasText(value) ? value!.Trim() : null;

    private static ContentPageSummary Map(ContentPage item) => new(item.Id, item.Kind, item.IsPublished, item.PublishedAtUtc, item.CoverImageUrl,
        item.ShowInFooter, item.SortOrder,
        new(item.TitleTr, item.SlugTr, item.SummaryTr, item.BodyTr, item.MetaTitleTr, item.MetaDescriptionTr),
        item.TitleEn is null ? null : new(item.TitleEn, item.SlugEn ?? "", item.SummaryEn, item.BodyEn ?? "", item.MetaTitleEn, item.MetaDescriptionEn),
        item.CreatedAtUtc, item.UpdatedAtUtc);
}
