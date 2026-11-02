using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/google-reviews")]
public sealed class GoogleReviewsController(IGoogleReviewService reviewService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<GoogleReviewSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GoogleReviewSummary>> Get(
        [FromQuery] string? language,
        CancellationToken cancellationToken
    )
    {
        var summary = await reviewService.GetAsync(language == "en" ? "en" : "tr", cancellationToken);
        if (summary is null) return NotFound();
        Response.Headers.CacheControl = "public,max-age=600";
        return Ok(summary);
    }
}
