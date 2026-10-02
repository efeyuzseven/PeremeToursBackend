using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PeremeTours.Application.Tours;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/tours")]
public sealed class ToursController(
    ITourCatalogService tourCatalogService,
    ITourContentService tourContentService,
    ITourBookingService tourBookingService,
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
    [ProducesResponseType<IReadOnlyList<PublicTourItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<PublicTourItem>>> List(
        CancellationToken cancellationToken
    ) => await ExecuteAsync(
        () => tourContentService.ListPublicAsync(cancellationToken)
    );

    [HttpGet("{externalTourId:int}/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetImage(
        [Range(1, int.MaxValue)] int externalTourId,
        CancellationToken cancellationToken
    )
    {
        var image = await tourContentService.GetImageAsync(
            externalTourId,
            cancellationToken
        );
        if (image is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return File(image.Content, image.ContentType);
    }

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
        Response.Headers.CacheControl = "no-store";
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

    [HttpPost("quote")]
    [EnableRateLimiting("tour-quote")]
    [ProducesResponseType<TourQuote>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TourQuote>> Quote(
        TourQuoteRequest request,
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "no-store";
        if (request.Tickets.Any(item => item is null))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Rezervasyon seçimi geçersiz", detail: "Bilet tipi ve adet bilgileri eksik.");
        }
        try
        {
            return Ok(await tourBookingService.QuoteAsync(new TourQuoteCommand(
                request.ExternalTourId,
                request.ExternalDeparturePortId,
                request.ExternalDepartureId,
                request.TourDate,
                request.Tickets.Select(item => new TourTicketSelection(item.ExternalPriceId, item.Quantity)).ToArray()
            ), cancellationToken));
        }
        catch (TourBookingValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Rezervasyon seçimi geçersiz", detail: exception.Message);
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

public sealed class TourQuoteRequest
{
    [Range(1, int.MaxValue)]
    public int ExternalTourId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalDeparturePortId { get; init; }

    [Range(1, int.MaxValue)]
    public int ExternalDepartureId { get; init; }

    public DateOnly TourDate { get; init; }

    [Required, MinLength(1), MaxLength(12)]
    public required IReadOnlyList<TourQuoteTicketRequest> Tickets { get; init; }
}

public sealed class TourQuoteTicketRequest
{
    [Range(1, int.MaxValue)]
    public int ExternalPriceId { get; init; }

    [Range(1, 12)]
    public int Quantity { get; init; }
}
