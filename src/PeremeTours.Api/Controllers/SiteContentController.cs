using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/site-content")]
public sealed class SiteContentController(
    IHomepageContentService homepageContentService, IHomepageImageService images
) : ControllerBase
{
    [HttpGet("homepage")]
    [ProducesResponseType<HomepageContentDocument>(StatusCodes.Status200OK)]
    public async Task<ActionResult<HomepageContentDocument>> GetHomepage(
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await homepageContentService.GetAsync(cancellationToken));
    }

    [HttpGet("homepage/images/{id:guid}")]
    public async Task<IActionResult> Image(Guid id, CancellationToken cancellationToken)
    {
        var image = await images.GetAsync(id, cancellationToken);
        if (image is null) return NotFound();
        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Content, image.ContentType);
    }
}
