using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.ContentManagers)]
[Route("api/v1/admin/tour-pages")]
public sealed class AdminTourPagesController(ITourPageService pages) : ControllerBase
{
    private const long MaximumImageSize = 8 * 1024 * 1024;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TourPageItem>>> List(CancellationToken cancellationToken)
    {
        try { return Ok(await pages.ListAdminAsync(cancellationToken)); }
        catch (TourCatalogUnavailableException) { return CatalogUnavailable(); }
    }

    [HttpGet("{key}")]
    public async Task<ActionResult<TourPageItem>> Get(string key, CancellationToken cancellationToken)
    {
        try
        {
            var page = await pages.GetAsync(key, true, cancellationToken);
            return page is null ? NotFound() : Ok(page);
        }
        catch (TourCatalogUnavailableException) { return CatalogUnavailable(); }
    }

    [HttpPut("{key}")]
    // Two languages of up to 50,000 characters each (multi-byte UTF-8) plus the rest of the page.
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<TourPageItem>> Update(string key, TourPageDocument request, CancellationToken cancellationToken)
    {
        if (request.Sections.Any(section => section is null))
            ModelState.AddModelError("sections", "Bölüm bilgileri eksik olamaz.");
        else
        {
            if (request.Sections.Select(section => section.Id).Distinct(StringComparer.Ordinal).Count() != request.Sections.Count)
                ModelState.AddModelError("sections", "Bölüm kimlikleri benzersiz olmalıdır.");
            foreach (var section in request.Sections)
            {
                if (section.ImageUrls.Any(url => !IsImageUrl(url))) ModelState.AddModelError("sections", "Görsel bağlantısı geçersiz.");
                if (section.Type == "map" ? !IsMapEmbedUrl(section.MapUrl) : section.MapUrl is not null)
                    ModelState.AddModelError("sections", "Harita için Google Haritalar yerleştirme bağlantısı gereklidir.");
                if (section.Type == "map" && !MapSectionIds.Contains(section.Id))
                    ModelState.AddModelError("sections", "Harita yalnızca buluşma noktası, güzergah veya servis noktası olabilir.");
                if (section.Tr.Items.Concat(section.En.Items).Concat(section.Tr.ExcludedItems ?? []).Concat(section.En.ExcludedItems ?? [])
                    .Any(item => item is null || item.Length > 400))
                    ModelState.AddModelError("sections", "Öne çıkan bir madde en fazla 400 karakter olabilir.");
            }
        }
        if ((request.Tr.BookingNotes ?? []).Concat(request.En.BookingNotes ?? []).Any(note => note is null))
            ModelState.AddModelError("bookingNotes", "Rezervasyon kartı notu eksik olamaz.");
        if (!string.IsNullOrEmpty(request.HeroImageUrl) && !IsImageUrl(request.HeroImageUrl))
            ModelState.AddModelError("heroImageUrl", "Görsel bağlantısı geçersiz.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var page = await pages.UpdateAsync(key, request, cancellationToken);
            return page is null ? NotFound() : Ok(page);
        }
        catch (TourCatalogUnavailableException) { return CatalogUnavailable(); }
    }

    [HttpPost("{key}/images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumImageSize + 64 * 1024)]
    public async Task<ActionResult<TourPageImageResult>> Upload(string key, [FromForm] TourImageUploadRequest request,
        CancellationToken cancellationToken)
    {
        var type = request.File.ContentType.ToLowerInvariant();
        if (request.File.Length is <= 0 or > MaximumImageSize || type is not ("image/jpeg" or "image/png" or "image/webp"))
            return Problem(statusCode: 400, title: "Görsel JPG, PNG veya WebP formatında ve en fazla 8 MB olmalıdır.");
        await using var buffer = new MemoryStream((int)request.File.Length);
        await request.File.CopyToAsync(buffer, cancellationToken);
        if (!AdminTourContentsController.HasValidSignature(buffer.ToArray(), type))
            return Problem(statusCode: 400, title: "Dosya içeriği belirtilen görsel formatıyla eşleşmiyor.");
        buffer.Position = 0;
        try
        {
            var result = await pages.UploadImageAsync(key, buffer, type, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (TourPageImageLimitException) { return Problem(statusCode: 400, title: "Bir tur sayfasında en fazla 40 görsel saklanabilir."); }
        catch (TourCatalogUnavailableException) { return CatalogUnavailable(); }
    }

    private ObjectResult CatalogUnavailable() => Problem(statusCode: 503, title: "Tur bilgileri şu anda alınamıyor.");

    // A page has at most these three maps; the id says which one it is.
    private static readonly HashSet<string> MapSectionIds = new(StringComparer.Ordinal) { "meeting-point", "route", "service-point" };

    // Only Google Maps embeds may be framed on the public site.
    private static bool IsMapEmbedUrl(string? value) => value is { Length: > 0 and <= 2000 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "www.google.com" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/maps/embed", StringComparison.Ordinal)
        && !value.Contains('\\') && !value.Contains('\r') && !value.Contains('\n') && !value.Contains('"');

    private static bool IsImageUrl(string? value) => value is { Length: > 0 and <= 2000 }
        && (value.StartsWith("/assets/", StringComparison.Ordinal) || value.StartsWith("/api/v1/tours/", StringComparison.Ordinal)
            || value.StartsWith("/api/v1/tour-pages/", StringComparison.Ordinal)
            || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        && !value.Contains('\\') && !value.Contains('\r') && !value.Contains('\n');
}
