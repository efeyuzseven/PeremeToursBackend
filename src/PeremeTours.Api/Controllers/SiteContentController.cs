using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/site-content")]
public sealed class SiteContentController(
    IHomepageContentService homepageContentService
) : ControllerBase
{
    [HttpGet("homepage")]
    [ProducesResponseType<HomepageContentDocument>(StatusCodes.Status200OK)]
    public async Task<ActionResult<HomepageContentDocument>> GetHomepage(
        CancellationToken cancellationToken
    ) => Ok(await homepageContentService.GetAsync(cancellationToken));
}
