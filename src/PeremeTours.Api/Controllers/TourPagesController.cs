using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/tour-pages")]
public sealed class TourPagesController(ITourPageService pages) : ControllerBase
{
    // A service page ("sunset") or a tour detail page by its number ("tour-2"). Anything else is tried as a tour
    // page's readable address, which is how site versions before the by-address route asked for it.
    [HttpGet("{key}")]
    public Task<ActionResult<TourPageItem>> Get(string key, CancellationToken cancellationToken)
        => Published(async () => await pages.GetAsync(key, false, cancellationToken) ?? await pages.GetBySlugAsync(key, cancellationToken));

    // A tour detail page by its readable address (pereme.com.tr/tur/{slug}). Kept apart from the keys above because an
    // address may spell the same as a service page's key ("sunset", "daytime") and must still open the tour page.
    [HttpGet("by-address/{slug}")]
    public Task<ActionResult<TourPageItem>> GetByAddress(string slug, CancellationToken cancellationToken)
        => Published(() => pages.GetBySlugAsync(slug, cancellationToken));

    private async Task<ActionResult<TourPageItem>> Published(Func<Task<TourPageItem?>> find)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var page = await find();
            return page is null ? NotFound() : Ok(page with
            {
                Document = page.Document with { Sections = page.Document.Sections.Where(section => section.IsVisible).ToArray() },
            });
        }
        catch (TourCatalogUnavailableException)
        {
            return Problem(statusCode: 503, title: "Tur bilgileri şu anda alınamıyor.");
        }
    }

    [HttpGet("{key}/images/{imageId:guid}")]
    public async Task<IActionResult> Image(string key, Guid imageId, CancellationToken cancellationToken)
    {
        var image = await pages.GetImageAsync(key, imageId, cancellationToken);
        if (image is null) return NotFound();
        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return File(image.Content, image.ContentType);
    }
}
