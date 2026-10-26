using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.RegularExpressions;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.ContentManagers)]
[Route("api/v1/admin/site-content")]
public sealed class AdminSiteContentController(
    IHomepageContentService homepageContentService, IHomepageImageService images
) : ControllerBase
{
    private static readonly HashSet<string> SupportedCategories =
        new(StringComparer.Ordinal)
        {
            TourCategoryKeys.TurkishNight,
            TourCategoryKeys.Sunset,
            TourCategoryKeys.Daytime,
            TourCategoryKeys.Bosphorus,
        };

    [HttpGet("homepage")]
    [ProducesResponseType<HomepageContentDocument>(StatusCodes.Status200OK)]
    public async Task<ActionResult<HomepageContentDocument>> GetHomepage(
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await homepageContentService.GetAsync(cancellationToken));
    }

    [HttpPost("homepage/images")]
    [EnableRateLimiting("homepage-image")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(8 * 1024 * 1024 + 64 * 1024)]
    public async Task<ActionResult<TourPageImageResult>> Upload([FromForm] TourImageUploadRequest request, CancellationToken cancellationToken)
    {
        var type = request.File.ContentType.ToLowerInvariant();
        if (request.File.Length is <= 0 or > 8 * 1024 * 1024 || type is not ("image/jpeg" or "image/png" or "image/webp"))
            return Problem(statusCode: 400, title: "Görsel JPG, PNG veya WebP formatında ve en fazla 8 MB olmalıdır.");
        await using var buffer = new MemoryStream((int)request.File.Length);
        await request.File.CopyToAsync(buffer, cancellationToken);
        if (!AdminTourContentsController.HasValidSignature(buffer.ToArray(), type))
            return Problem(statusCode: 400, title: "Dosya içeriği belirtilen görsel formatıyla eşleşmiyor.");
        buffer.Position = 0;
        return Ok(await images.UploadAsync(buffer, type, cancellationToken));
    }

    [HttpPut("homepage")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [ProducesResponseType<HomepageContentDocument>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(
        StatusCodes.Status400BadRequest
    )]
    public async Task<ActionResult<HomepageContentDocument>> UpdateHomepage(
        HomepageContentDocument request,
        CancellationToken cancellationToken
    )
    {
        ValidateLanguage(request.Tr, "tr");
        ValidateLanguage(request.En, "en");
        var imageKeys = new[] { "hero", "final", "storyMain", "storySmall", "turkish-night", "sunset", "daytime", "bosphorus" };
        if (request.Images is { } uploaded && (uploaded.Count > 8 || uploaded.Any(item => !imageKeys.Contains(item.Key) || !IsImageUrl(item.Value))))
            ModelState.AddModelError("images", "Görsel alanı veya bağlantısı geçersiz.");
        if (request.ImagePositions is { } positions && (positions.Count > 8 || positions.Any(item => !imageKeys.Contains(item.Key)
                || !new[] { "center", "top", "bottom", "left", "right" }.Contains(item.Value))))
            ModelState.AddModelError("imagePositions", "Görsel odak noktası geçersiz.");
        if (request.HiddenSections is { } hidden && (hidden.Count > 7 || hidden.Distinct().Count() != hidden.Count
                || hidden.Any(item => !new[] { "marquee", "services", "announcements", "why", "stories", "final", "heroCard" }.Contains(item))))
            ModelState.AddModelError("hiddenSections", "Bölüm görünürlüğü geçersiz.");
        ValidateAnnouncements(request.Announcements);
        if (request.StoryPhotos is { } photos && (photos.Count > 6 || photos.Any(url => string.IsNullOrEmpty(url) || !IsImageUrl(url))))
            ModelState.AddModelError("storyPhotos", "En fazla 6 fotoğraf eklenebilir ve fotoğraflar site görsel deposundan olmalıdır.");
        if (request.InstagramUrls is null) return Problem(statusCode: 400, title: "Instagram bağlantıları eksik olamaz.");
        if (request.InstagramUrls.Count > 6)
        {
            ModelState.AddModelError(
                nameof(request.InstagramUrls),
                "En fazla 6 Instagram bağlantısı eklenebilir."
            );
        }
        foreach (var url in request.InstagramUrls)
        {
            if (!IsInstagramUrl(url))
            {
                ModelState.AddModelError(
                    nameof(request.InstagramUrls),
                    "Yalnızca instagram.com üzerindeki Reel veya gönderi bağlantıları kullanılabilir."
                );
                break;
            }
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return Ok(await homepageContentService.UpdateAsync(
            request,
            cancellationToken
        ));
    }

    private void ValidateLanguage(
        HomepageLanguageContent content,
        string fieldPrefix
    )
    {
        if (content is null || content.Hero is null || content.Tours is null || content.Services?.Items is null
            || content.Why?.Benefits is null || content.Stories is null || content.Final is null
            || content.Services.Items.Any(item => item is null) || content.Why.Benefits.Any(item => item is null))
        {
            ModelState.AddModelError(fieldPrefix, "İçerik alanları eksik olamaz.");
            return;
        }
        if (
            content.Services.Items.Count != 4
            || content.Services.Items
                .Select(item => item.CategoryKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != 4
            || content.Services.Items.Any(
                item => !SupportedCategories.Contains(item.CategoryKey)
            )
        )
        {
            ModelState.AddModelError(
                $"{fieldPrefix}.services.items",
                "Hizmetler dört desteklenen tur kategorisini birer kez içermelidir."
            );
        }
        if (content.Why.Benefits.Count != 4)
        {
            ModelState.AddModelError(
                $"{fieldPrefix}.why.benefits",
                "Neden Pereme alanı dört fayda içermelidir."
            );
        }

        if (content.Texts is { } texts && (texts.Count > 100 || texts.Any(item => item.Value is null || item.Value.Length > 3000
                || !Regex.IsMatch(item.Key, "^[a-zA-Z][a-zA-Z0-9.]{0,79}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))))
            ModelState.AddModelError($"{fieldPrefix}.texts", "Ek metin alanı geçersiz veya 3000 karakterden uzun.");

        if (EnumerateText(content).Any(text => text is null || text.Length > 3000))
        {
            ModelState.AddModelError(
                fieldPrefix,
                "Tek bir içerik alanı 3000 karakterden uzun olamaz."
            );
        }
    }

    private static bool IsImageUrl(string? value)
    {
        if (value == "") return true; // Explicit reset to the current design default.
        if (value is not { Length: > 0 and <= 2000 } || value.Contains('\\') || value.Any(char.IsControl)
            || value.Contains('%') || value.Contains("..", StringComparison.Ordinal) || value.Contains('?') || value.Contains('#')) return false;
        return Regex.IsMatch(value, "^/assets/[a-zA-Z0-9_/-]+\\.(jpg|jpeg|png|webp)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            || value.StartsWith("/api/v1/site-content/homepage/images/", StringComparison.Ordinal)
                && Guid.TryParse(value["/api/v1/site-content/homepage/images/".Length..], out _);
    }

    private static IEnumerable<string> EnumerateText(
        HomepageLanguageContent content
    )
    {
        yield return content.Hero.Lead;
        yield return content.Hero.Accent;
        yield return content.Hero.Description;
        yield return content.Tours.Eyebrow;
        yield return content.Tours.Lead;
        yield return content.Tours.Accent;
        yield return content.Tours.Description;
        yield return content.Services.Eyebrow;
        yield return content.Services.Lead;
        yield return content.Services.Accent;
        yield return content.Services.Description;
        yield return content.Why.Eyebrow;
        yield return content.Why.Lead;
        yield return content.Why.Accent;
        yield return content.Why.Reviews;
        yield return content.Stories.Eyebrow;
        yield return content.Stories.Lead;
        yield return content.Stories.Accent;
        yield return content.Stories.Description;
        yield return content.Stories.Quote;
        yield return content.Stories.Meta;
        yield return content.Final.Pre;
        yield return content.Final.Title;
        yield return content.Final.Action;
        foreach (var item in content.Services.Items)
        {
            yield return item.Title;
            yield return item.Description;
            yield return item.BrowseLabel;
            yield return item.BookingLabel;
        }
        foreach (var benefit in content.Why.Benefits)
        {
            yield return benefit.Title;
            yield return benefit.Description;
        }
    }

    private void ValidateAnnouncements(IReadOnlyList<HomepageAnnouncement>? announcements)
    {
        if (announcements is null) return;
        const string Field = "announcements";
        if (announcements.Count > 12) { ModelState.AddModelError(Field, "En fazla 12 duyuru eklenebilir."); return; }
        if (announcements.Any(item => item is null || item.Tr is null || item.En is null))
        {
            ModelState.AddModelError(Field, "Duyuru bilgileri eksik olamaz.");
            return;
        }
        if (announcements.Any(item => item.Id is null || !Regex.IsMatch(item.Id, "^[a-zA-Z0-9_-]{1,80}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            || announcements.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != announcements.Count)
            ModelState.AddModelError(Field, "Duyuru kimlikleri geçersiz veya tekrarlı.");
        if (announcements.Any(item => string.IsNullOrWhiteSpace(item.Tr.Title)))
            ModelState.AddModelError(Field, "Her duyurunun Türkçe başlığı olmalıdır.");
        if (announcements.SelectMany(item => new[] { item.Tr, item.En }).Any(text => text.Title?.Length > 160 || text.Body?.Length > 1500 || text.LinkLabel?.Length > 60))
            ModelState.AddModelError(Field, "Duyuru başlığı en fazla 160, metni en fazla 1500, bağlantı yazısı en fazla 60 karakter olabilir.");
        if (announcements.Any(item => item.Date is not null && !DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", out _)))
            ModelState.AddModelError(Field, "Duyuru tarihi geçersiz.");
        if (announcements.Any(item => item.ImageUrl is not null && (item.ImageUrl == "" || !IsImageUrl(item.ImageUrl))))
            ModelState.AddModelError(Field, "Duyuru görseli geçersiz.");
        if (announcements.Any(item => item.LinkUrl is not null && !IsAnnouncementLink(item.LinkUrl)))
            ModelState.AddModelError(Field, "Duyuru bağlantısı https:// ile ya da site içi bir adresle (/...) başlamalıdır.");
    }

    // Absolute https links or paths on this site; never javascript:, data: or protocol-relative URLs.
    private static bool IsAnnouncementLink(string value) => value is { Length: > 0 and <= 500 } && !value.Any(char.IsControl) && !value.Contains('\\')
        && (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsInstagramUrl(string value)
    {
        if (value is not { Length: > 0 and <= 2000 } || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }
        var validHost = uri.Host.Equals(
            "instagram.com",
            StringComparison.OrdinalIgnoreCase
        ) || uri.Host.EndsWith(
            ".instagram.com",
            StringComparison.OrdinalIgnoreCase
        );
        return uri.Scheme == Uri.UriSchemeHttps
            && validHost
            && (uri.AbsolutePath.StartsWith("/reel/", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/p/", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/tv/", StringComparison.OrdinalIgnoreCase));
    }
}
