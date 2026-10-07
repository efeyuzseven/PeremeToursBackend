using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/account")]
public sealed class AccountController(PeremeToursDbContext db) : ControllerBase
{
    [HttpGet("profile")]
    public async Task<ActionResult<AccountProfile>> Profile(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        var user = await db.Users.AsNoTracking().Where(item => item.Id == userId && item.IsActive)
            .Select(item => new AccountProfile(item.FirstName, item.LastName, item.Email, item.CreatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? Unauthorized() : Ok(user);
    }

    [HttpGet("reservations")]
    public async Task<ActionResult<IReadOnlyList<AccountReservation>>> Reservations(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        if (!await db.Users.AnyAsync(item => item.Id == userId && item.IsActive, cancellationToken)) return Unauthorized();
        // Never infer ownership from contact email: registration currently has no email verification.
        var reservations = await db.TourTickets.AsNoTracking().Where(item => item.UserId == userId)
            .Include(item => item.Passengers).Include(item => item.PaymentEmail).Include(item => item.Cancellation)
            .OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        return Ok(reservations.Select(item => new AccountReservation(item.Id, item.TicketCode,
            item.TourName, item.TourDate, item.DepartureTime, item.DeparturePortName, item.GuestCount,
            item.Amount, item.Currency, item.Status.ToString(), item.PaymentStatus.ToString(),
            item.TicketingStatus.ToString(), item.PaymentEmail?.Status.ToString(), item.Cancellation?.DisplayStatus(DateTime.UtcNow).ToString(),
            item.Passengers.OrderBy(passenger => passenger.Sequence).Select(passenger =>
                new AccountPassenger(passenger.Sequence + 1, passenger.FirstName + " " + passenger.LastName,
                    passenger.Pnr, item.Cancellation is null && item.Status == TicketStatus.Confirmed && item.PaymentStatus == TicketPaymentStatus.Paid
                        && item.TicketingStatus == TicketingStatus.Issued ? passenger.ExternalTicketGuid : null)).ToArray())).ToArray());
    }
}

public sealed record AccountProfile(string FirstName, string? LastName, string Email, DateTimeOffset CreatedAtUtc);
public sealed record AccountPassenger(int Number, string Name, string? Pnr, string? TicketGuid);
public sealed record AccountReservation(Guid Id, string TicketCode, string TourName, DateOnly TourDate,
    TimeOnly DepartureTime, string DeparturePort, int GuestCount, decimal Amount, string Currency,
    string Status, string PaymentStatus, string TicketingStatus, string? EmailStatus, string? CancellationStatus, IReadOnlyList<AccountPassenger> Passengers);
