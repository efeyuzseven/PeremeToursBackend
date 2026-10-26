using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;

namespace PeremeTours.Api.Controllers;

// Published blog posts and standalone pages for the public site.
[ApiController]
[AllowAnonymous]
[Route("api/v1")]
public sealed class ContentPagesController(IContentPageService pages) : ControllerBase
{
    [HttpGet("blog")]
    public async Task<ActionResult<PublicContentPageList>> ListBlog([FromQuery] string language = "tr", [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12, CancellationToken cancellationToken = default)
    {
        if (!IsLanguage(language) || page is < 1 or > 1000 || pageSize is < 1 or > 50) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await pages.ListPublishedAsync(ContentPageKinds.Blog, language, page, pageSize, cancellationToken));
    }

    [HttpGet("blog/{slug}")]
    public Task<ActionResult<PublicContentPage>> GetBlogPost(string slug, [FromQuery] string language = "tr", CancellationToken cancellationToken = default)
        => Get(ContentPageKinds.Blog, slug, language, cancellationToken);

    [HttpGet("pages")]
    public async Task<ActionResult<IReadOnlyList<ContentPageLink>>> ListFooterPages([FromQuery] string language = "tr", CancellationToken cancellationToken = default)
    {
        if (!IsLanguage(language)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await pages.ListFooterPagesAsync(language, cancellationToken));
    }

    [HttpGet("pages/{slug}")]
    public Task<ActionResult<PublicContentPage>> GetPage(string slug, [FromQuery] string language = "tr", CancellationToken cancellationToken = default)
        => Get(ContentPageKinds.Page, slug, language, cancellationToken);

    private async Task<ActionResult<PublicContentPage>> Get(string kind, string slug, string language, CancellationToken cancellationToken)
    {
        if (!IsLanguage(language) || slug.Length > 200) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        var item = await pages.GetPublishedAsync(kind, language, slug, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    private static bool IsLanguage(string language) => language is "tr" or "en";
}
