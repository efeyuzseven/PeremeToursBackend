using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;

namespace PeremeTours.Api.Controllers;

// sitemap.xml for search engines. The site's robots.txt points here because the static site cannot build it.
[ApiController]
[AllowAnonymous]
public sealed class SitemapController(IContentPageService pages, ITourContentService tours, IConfiguration configuration,
    ILogger<SitemapController> logger) : ControllerBase
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly string[] StaticPaths = ["/", "/blog", "/iletisim", "/sikca-sorulan-sorular", "/turlar/turk-gecesi", "/turlar/sunset",
        "/turlar/daytime", "/turlar/bogaz-turu", "/kvkk-aydinlatma-metni", "/mesafeli-satis-sozlesmesi"];

    private static readonly Action<ILogger, Exception?> LogToursUnavailable = LoggerMessage.Define(LogLevel.Warning,
        new EventId(4101, nameof(LogToursUnavailable)), "Tour catalog unavailable while building sitemap.xml; tour pages were left out.");

    [HttpGet("/sitemap.xml")]
    [Produces("application/xml")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["Site:BaseUrl"] ?? "https://pereme.com.tr").TrimEnd('/');
        var urls = StaticPaths.Select(path => SitemapUrl(baseUrl + path)).ToList();
        try
        {
            urls.AddRange((await tours.ListPublicAsync(cancellationToken))
                .Select(tour => SitemapUrl($"{baseUrl}/tur/{tour.ExternalTourId.ToString(CultureInfo.InvariantCulture)}")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogToursUnavailable(logger, exception);
        }
        foreach (var entry in await pages.ListSitemapEntriesAsync(cancellationToken))
        {
            var prefix = entry.Kind == ContentPageKinds.Blog ? "/blog/" : "/";
            var tr = $"{baseUrl}{prefix}{entry.SlugTr}";
            var en = entry.SlugEn is null ? null : $"{baseUrl}/en{prefix}{entry.SlugEn}";
            urls.Add(SitemapUrl(tr, entry.UpdatedAtUtc, tr, en));
            if (en is not null) urls.Add(SitemapUrl(en, entry.UpdatedAtUtc, tr, en));
        }
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "urlset", new XAttribute(XNamespace.Xmlns + "xhtml", Xhtml), urls));
        Response.Headers.CacheControl = "public, max-age=3600";
        return Content(document.Declaration + Environment.NewLine + document.Root, "application/xml; charset=utf-8");
    }

    // Translated pages list each other as alternates (hreflang).
    private static XElement SitemapUrl(string location, DateTimeOffset? modified = null, string? tr = null, string? en = null)
    {
        var element = new XElement(Ns + "url", new XElement(Ns + "loc", location));
        if (modified is { } date) element.Add(new XElement(Ns + "lastmod", date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (tr is not null && en is not null)
            element.Add(Alternate("tr", tr), Alternate("en", en));
        return element;
    }

    private static XElement Alternate(string language, string href) =>
        new(Xhtml + "link", new XAttribute("rel", "alternate"), new XAttribute("hreflang", language), new XAttribute("href", href));
}
