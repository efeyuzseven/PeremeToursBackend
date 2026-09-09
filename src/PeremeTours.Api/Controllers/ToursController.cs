using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Tours;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/tours")]
public sealed class ToursController(
    ITourCatalogService tourCatalogService,
    ILogger<ToursController> logger
) : ControllerBase
{
    private static readonly Action<ILogger, Exception?> LogCatalogUnavailable =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2001, nameof(LogCatalogUnavailable)),
            "Tour catalog is unavailable."
        );

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TourCatalogItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<TourCatalogItem>>> List(
        CancellationToken cancellationToken
    ) => await ExecuteAsync(
        () => tourCatalogService.ListAsync(cancellationToken)
    );

    [HttpGet("{externalTourId:int}/ports")]
    [ProducesResponseType<IReadOnlyList<TourPort>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<TourPort>>> ListPorts(
        [Range(1, int.MaxValue)] int externalTourId,
        CancellationToken cancellationToken
    ) => await ExecuteAsync(
        () => tourCatalogService.ListPortsAsync(
            externalTourId,
            cancellationToken
        )
    );

    [HttpGet("{externalTourId:int}/availability")]
    [ProducesResponseType<TourAvailability>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TourAvailability>> GetAvailability(
        [Range(1, int.MaxValue)] int externalTourId,
        [FromQuery, Range(1, int.MaxValue)] int departurePortId,
        [FromQuery, Range(1, 3)] int saleType = 2,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var availability = await tourCatalogService.GetAvailabilityAsync(
                externalTourId,
                departurePortId,
                saleType,
                cancellationToken
            );
            return availability is null ? NotFound() : Ok(availability);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (TourCatalogUnavailableException exception)
        {
            return CatalogUnavailable(exception);
        }
    }

    private async Task<ActionResult<T>> ExecuteAsync<T>(
        Func<Task<T>> action
    )
    {
        try
        {
            return Ok(await action());
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (TourCatalogUnavailableException exception)
        {
            return CatalogUnavailable(exception);
        }
    }

    private ObjectResult CatalogUnavailable(
        TourCatalogUnavailableException exception
    )
    {
        LogCatalogUnavailable(logger, exception);
        return Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Tur servisine ulaşılamıyor",
            detail: exception.Message
        );
    }
}
