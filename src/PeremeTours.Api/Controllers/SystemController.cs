using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController(PeremeToursDbContext dbContext) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health() =>
        Ok(new { status = "healthy", service = "PeremeTours.Api" });

    [AllowAnonymous]
    [HttpGet("ready")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (await dbContext.Database.CanConnectAsync(timeout.Token))
                return Ok(new { status = "ready", service = "PeremeTours.Api" });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Report a bounded readiness failure, without credentials or exception details.
        }
        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { status = "not_ready", service = "PeremeTours.Api" });
    }
}
