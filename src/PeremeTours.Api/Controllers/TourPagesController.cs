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
    [HttpGet("{key}")]
    public async Task<ActionResult<TourPageItem>> Get(string key, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var page = await pages.GetAsync(key, false, cancellationToken);
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
