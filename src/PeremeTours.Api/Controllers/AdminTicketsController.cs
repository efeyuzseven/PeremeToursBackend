using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/tickets")]
public sealed class AdminTicketsController(ITicketService ticketService)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TicketSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TicketSummary>>> List(
        CancellationToken cancellationToken
    ) => Ok(await ticketService.ListAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType<TicketSummary>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TicketSummary>> Create(
        CreateTicketRequest request,
        CancellationToken cancellationToken
    )
    {
        var actorId = Guid.Parse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!
        );
        var ticket = await ticketService.CreateAsync(
            actorId,
            new CreateTicketCommand(
                request.TourName,
                request.TourDate,
                request.DepartureTime,
                request.CustomerName,
                request.CustomerEmail,
                request.GuestCount,
                request.Amount,
                request.Status,
                request.Channel
            ),
            cancellationToken
        );
        return CreatedAtAction(nameof(List), new { id = ticket.Id }, ticket);
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType<TicketSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketSummary>> Update(
        Guid id,
        UpdateTicketRequest request,
        CancellationToken cancellationToken
    )
    {
        var ticket = await ticketService.UpdateAsync(
            id,
            new UpdateTicketCommand(
                request.TourName,
                request.TourDate,
                request.DepartureTime,
                request.CustomerName,
                request.CustomerEmail,
                request.GuestCount,
                request.Amount,
                request.Status
            ),
            cancellationToken
        );
        return ticket is null ? NotFound() : Ok(ticket);
    }
}

public sealed class CreateTicketRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public required string TourName { get; init; }

    public DateOnly TourDate { get; init; }

    public TimeOnly DepartureTime { get; init; }

    [Required, StringLength(160, MinimumLength = 1)]
    public required string CustomerName { get; init; }

    [Required, EmailAddress, StringLength(320)]
    public required string CustomerEmail { get; init; }

    [Range(1, 100)]
    public int GuestCount { get; init; }

    [Range(typeof(decimal), "0.01", "9999999999")]
    public decimal Amount { get; init; }

    public TicketStatus Status { get; init; } = TicketStatus.Confirmed;

    public TicketChannel Channel { get; init; } = TicketChannel.Admin;
}

public sealed class UpdateTicketRequest
{
    [StringLength(160, MinimumLength = 1)]
    public string? TourName { get; init; }

    public DateOnly? TourDate { get; init; }

    public TimeOnly? DepartureTime { get; init; }

    [StringLength(160, MinimumLength = 1)]
    public string? CustomerName { get; init; }

    [EmailAddress, StringLength(320)]
    public string? CustomerEmail { get; init; }

    [Range(1, 100)]
    public int? GuestCount { get; init; }

    [Range(typeof(decimal), "0.01", "9999999999")]
    public decimal? Amount { get; init; }

    public TicketStatus? Status { get; init; }
}
