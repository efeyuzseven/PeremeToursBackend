using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

public sealed class KoalaOptions
{
    public const string SectionName = "Koala";

    // The "Signing Secret" of the Custom Webhook integration in Koala; deliveries are refused while it is empty.
    public string SigningSecret { get; init; } = string.Empty;

    // False keeps new posts as drafts until someone publishes them in the admin panel.
    public bool PublishImmediately { get; init; }

    // Hosts the cover image may be downloaded from (subdomains included).
    public string[] ImageHosts { get; init; } = ["koala.sh"];
}

// Koala documents only id, config.targetKeyword and output.html / output.polishedHtml; the article's title is
// the first <h1> of the HTML. Slug, SEO texts and a featured image are used when the payload carries them.
internal sealed partial class KoalaWebhookReceiver(
    PeremeToursDbContext db,
    IHomepageImageService images,
    HttpClient httpClient,
    IOptions<KoalaOptions> options,
    TimeProvider clock,
    ILogger<KoalaWebhookReceiver> logger) : IKoalaWebhookReceiver
{
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly string[] SlugFields = ["slug"];
    private static readonly string[] MetaTitleFields = ["seoTitle", "metaTitle"];
    private static readonly string[] MetaDescriptionFields = ["metaDescription", "seoDescription"];
    private static readonly string[] ImageFields = ["featuredImage", "featuredImageUrl", "coverImage", "image"];
    private static readonly Action<ILogger, string, string, string?, Exception?> LogReceived =
        LoggerMessage.Define<string, string, string?>(LogLevel.Information, new EventId(5201, nameof(LogReceived)),
            "Koala article {ArticleId}: {Status}. DeliveryId={DeliveryId}");
    private static readonly Action<ILogger, string?, Exception?> LogRejected =
        LoggerMessage.Define<string?>(LogLevel.Warning, new EventId(5202, nameof(LogRejected)),
            "Koala webhook rejected: {Reason}");
    private static readonly Action<ILogger, string, Exception?> LogImageFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5203, nameof(LogImageFailed)),
            "Koala cover image could not be stored for article {ArticleId}; the post is saved without it.");

    private readonly KoalaOptions _options = options.Value;

    public async Task<KoalaWebhookResult> ReceiveAsync(byte[] body, string? signature, string? eventName, string? deliveryId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SigningSecret)) return Reject(KoalaWebhookStatus.NotConfigured, "no signing secret");
        if (!IsValidSignature(body, signature, _options.SigningSecret)) return Reject(KoalaWebhookStatus.BadSignature, "bad signature");

        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { return Reject(KoalaWebhookStatus.InvalidPayload, "not JSON"); }
        using var _ = document;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Reject(KoalaWebhookStatus.InvalidPayload, "not an object");
        if (eventName == "article.test" || Text(root, "event") == "article.test") return new(KoalaWebhookStatus.Test);

        var articleId = root.TryGetProperty("id", out var id) ? id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        } : null;
        var config = Child(root, "config");
        var output = Child(root, "output");
        var html = Text(output, "polishedHtml") ?? Text(output, "html");
        if (string.IsNullOrWhiteSpace(articleId) || articleId.Length > 90 || html is null)
            return Reject(KoalaWebhookStatus.InvalidPayload, "id or output.html missing");

        if (!IsTurkish(Text(config, "language")))
        {
            LogReceived(logger, articleId, "skipped (not Turkish)", deliveryId, null);
            return new(KoalaWebhookStatus.Skipped, Detail: "Yalnızca Türkçe makaleler alınır.");
        }

        var heading = FirstHeading().Match(html);
        var title = heading.Success ? PlainText(heading.Groups[1].Value) : Text(config, "title")?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return Reject(KoalaWebhookStatus.InvalidPayload, "no title");
        var bodyHtml = heading.Success ? html.Remove(heading.Index, heading.Length) : html;
        var firstParagraph = FirstParagraph().Match(bodyHtml) is { Success: true } paragraph ? PlainText(paragraph.Groups[1].Value) : null;

        var externalId = $"koala:{articleId}";
        var page = await db.ContentPages.SingleOrDefaultAsync(item => item.ExternalId == externalId, cancellationToken);

        // A featured image from the payload, otherwise the article's first picture, which then leaves the body.
        var inlineImage = FirstImage().Match(bodyHtml);
        var imageUrl = Field(output, config, ImageFields)
            ?? (inlineImage.Success ? WebUtility.HtmlDecode(inlineImage.Groups[1].Value) : null);
        var coverUrl = page?.CoverImageUrl ?? (imageUrl is null ? null : await StoreImageAsync(imageUrl, articleId, cancellationToken));
        if (coverUrl is not null && Field(output, config, ImageFields) is null && inlineImage.Success)
            bodyHtml = bodyHtml.Remove(inlineImage.Index, inlineImage.Length);

        var now = clock.GetUtcNow();
        var created = page is null;
        if (page is null)
        {
            page = new ContentPage
            {
                Id = Guid.NewGuid(), Kind = ContentPageKinds.Blog, ExternalId = externalId, TitleTr = "", BodyTr = "",
                SlugTr = "", IsPublished = _options.PublishImmediately, PublishedAtUtc = _options.PublishImmediately ? now : null,
                CreatedAtUtc = now,
            };
            // The address stays fixed afterwards so links to a published post keep working.
            page.SlugTr = await FreeSlugAsync(Field(output, config, SlugFields) ?? title, page.Id, cancellationToken);
            db.ContentPages.Add(page);
        }

        page.TitleTr = Limit(title, 200);
        page.BodyTr = bodyHtml.Trim();
        page.SummaryTr = firstParagraph is null ? null : Limit(firstParagraph, 300);
        page.MetaTitleTr = Field(output, config, MetaTitleFields) is { } metaTitle ? Limit(PlainText(metaTitle), 70)
            : title.Length <= 70 ? title : null;
        page.MetaDescriptionTr = Field(output, config, MetaDescriptionFields) is { } description ? Limit(PlainText(description), 200)
            : firstParagraph is null ? null : Limit(firstParagraph, 160);
        page.CoverImageUrl = coverUrl;
        page.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);

        var status = created ? KoalaWebhookStatus.Created : KoalaWebhookStatus.Updated;
        LogReceived(logger, articleId, status.ToString(), deliveryId, null);
        return new(status, page.Id, page.SlugTr);
    }

    internal static bool IsValidSignature(byte[] body, string? header, string secret)
    {
        if (header is null || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] received;
        try { received = Convert.FromHexString(header.AsSpan(7).Trim()); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }

    private KoalaWebhookResult Reject(KoalaWebhookStatus status, string reason)
    {
        LogRejected(logger, reason, null);
        return new(status, Detail: reason);
    }

    private async Task<string?> StoreImageAsync(string url, string articleId, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !_options.ImageHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            return null;
        try
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes) return null;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxImageBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            if (ImageSignature.Detect(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) is not { } contentType) return null;
            buffer.Position = 0;
            return (await images.UploadAsync(buffer, contentType, cancellationToken)).Url;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not DbUpdateException)
        {
            LogImageFailed(logger, articleId, exception);
            return null;
        }
    }

    private async Task<string> FreeSlugAsync(string source, Guid id, CancellationToken cancellationToken)
    {
        var slug = ContentSlug.Create(source);
        if (slug.Length == 0) slug = $"yazi-{id.ToString("N")[..8]}";
        var stem = slug.Length <= 116 ? slug : slug[..116].TrimEnd('-');
        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? slug : $"{stem}-{attempt}";
            if (!await db.ContentPages.AnyAsync(item => item.Kind == ContentPageKinds.Blog && item.SlugTr == candidate, cancellationToken))
                return candidate;
        }
    }

    // Koala names the language in words ("Turkish") or as a code; an article without one is taken as Turkish.
    private static bool IsTurkish(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Trim().ToLowerInvariant() is "turkish" or "türkçe" or "turkce" or "tr" or "tr-tr";

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static string? Text(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;

    // The first of the names that output or config carries as text (an image may also be an object with a url).
    private static string? Field(JsonElement output, JsonElement config, string[] names)
    {
        foreach (var source in new[] { output, config })
        foreach (var name in names)
        {
            if (Text(source, name) is { } text && !string.IsNullOrWhiteSpace(text)) return text.Trim();
            if (Child(source, name) is { ValueKind: JsonValueKind.Object } nested && Text(nested, "url") is { } url) return url.Trim();
        }
        return null;
    }

    private static string PlainText(string html) =>
        Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();

    // Cuts at a word boundary and marks the cut.
    private static string Limit(string value, int length)
    {
        if (value.Length <= length) return value;
        var cut = value[..(length - 1)];
        var space = cut.LastIndexOf(' ');
        return $"{(space > length / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':')}…";
    }

    [GeneratedRegex(@"<h1\b[^>]*>(.*?)</h1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 200)]
    private static partial Regex FirstHeading();

    [GeneratedRegex(@"<p\b[^>]*>(.*?)</p\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 200)]
    private static partial Regex FirstParagraph();

    [GeneratedRegex(@"<img\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase, 200)]
    private static partial Regex FirstImage();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, 200)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+", RegexOptions.None, 200)]
    private static partial Regex Whitespace();
}
