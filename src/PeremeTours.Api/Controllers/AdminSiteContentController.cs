using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/site-content")]
public sealed class AdminSiteContentController(
    IHomepageContentService homepageContentService
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
    ) => Ok(await homepageContentService.GetAsync(cancellationToken));

    [HttpPut("homepage")]
    [RequestSizeLimit(128 * 1024)]
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

        if (EnumerateText(content).Any(text => text.Length > 3000))
        {
            ModelState.AddModelError(
                fieldPrefix,
                "Tek bir içerik alanı 3000 karakterden uzun olamaz."
            );
        }
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

    private static bool IsInstagramUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
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
