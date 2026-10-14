using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Domain.Tickets;
using PeremeTours.Application.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/account")]
public sealed class AccountController(PeremeToursDbContext db, IAccountManagementService accounts) : ControllerBase
{
    [HttpGet("profile")]
    public async Task<ActionResult<AccountDetails>> Profile(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        var user = await accounts.GetProfileAsync(userId, cancellationToken);
        return user is null ? Unauthorized() : Ok(user);
    }

    [HttpPatch("profile")]
    [EnableRateLimiting("account-security")]
    public async Task<IActionResult> UpdateProfile(UpdateAccountRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return OperationResponse(await accounts.UpdateProfileAsync(userId, User.FindFirstValue("auth_stamp"),
            new(request.FirstName, request.LastName, request.Email, request.CurrentPassword), cancellationToken));
    }

    [HttpPost("password")]
    [EnableRateLimiting("account-security")]
    public async Task<IActionResult> ChangePassword(ChangeAccountPasswordRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return OperationResponse(await accounts.ChangePasswordAsync(userId, User.FindFirstValue("auth_stamp"),
            request.CurrentPassword, request.NewPassword, cancellationToken));
    }

    [HttpDelete]
    [EnableRateLimiting("account-security")]
    public async Task<IActionResult> DeleteAccount(DeleteAccountRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return OperationResponse(await accounts.DeleteAccountAsync(userId, User.FindFirstValue("auth_stamp"),
            request.CurrentPassword, request.Confirmed, cancellationToken));
    }

    private IActionResult OperationResponse(AccountOperationResult result)
    {
        if (result.Status == AccountOperationStatus.Updated) return Ok(new { result.RequiresLogin });
        if (result.Status == AccountOperationStatus.Unauthorized) return Unauthorized();
        var (code, status) = result.Status switch
        {
            AccountOperationStatus.InvalidProfile => ("INVALID_ACCOUNT_PROFILE", 400),
            AccountOperationStatus.InvalidPassword => ("INVALID_NEW_PASSWORD", 400),
            AccountOperationStatus.PasswordIncorrect => ("CURRENT_PASSWORD_INCORRECT", 400),
            AccountOperationStatus.EmailAlreadyExists => ("ACCOUNT_EMAIL_IN_USE", 409),
            AccountOperationStatus.EmailProtected => ("ACCOUNT_EMAIL_PROTECTED", 409),
            AccountOperationStatus.DeletionProtected => ("ACCOUNT_DELETION_PROTECTED", 409),
            AccountOperationStatus.LastAdministrator => ("LAST_ADMINISTRATOR", 409),
            AccountOperationStatus.ConfirmationRequired => ("ACCOUNT_DELETE_CONFIRMATION_REQUIRED", 400),
            _ => ("ACCOUNT_UPDATE_CONFLICT", 409),
        };
        return Problem(title: code, statusCode: status);
    }

    [HttpGet("reservations")]
    public async Task<ActionResult<IReadOnlyList<AccountReservation>>> Reservations(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        if (!await db.Users.AnyAsync(item => item.Id == userId && item.IsActive && !item.IsDeleted, cancellationToken)) return Unauthorized();
        // Never infer ownership from contact email: registration currently has no email verification.
        var reservations = await db.TourTickets.AsNoTracking().Where(item => item.UserId == userId)
            .Include(item => item.Passengers).Include(item => item.PaymentEmail).Include(item => item.Cancellation)
            .Include(item => item.CancellationEmail)
            .OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        return Ok(reservations.Select(item => new AccountReservation(item.Id, item.TicketCode,
            item.TourName, item.TourDate, item.DepartureTime, item.DeparturePortName, item.GuestCount,
            item.Amount, item.Currency, item.Status.ToString(), item.PaymentStatus.ToString(),
            item.TicketingStatus.ToString(), item.PaymentEmail?.Status.ToString(), item.Cancellation?.DisplayStatus(DateTime.UtcNow).ToString(),
            item.Passengers.OrderBy(passenger => passenger.Sequence).Select(passenger =>
                new AccountPassenger(passenger.Sequence + 1, passenger.FirstName + " " + passenger.LastName,
                    passenger.Pnr, item.Cancellation is null && item.Status == TicketStatus.Confirmed && item.PaymentStatus == TicketPaymentStatus.Paid
                        && item.TicketingStatus == TicketingStatus.Issued ? passenger.ExternalTicketGuid : null)).ToArray(),
            item.CancellationEmail?.Status.ToString(), item.CancellationEmail?.SentAtUtc)).ToArray());
    }
}

public sealed record UpdateAccountRequest(
    [Required, StringLength(100)] string FirstName,
    [StringLength(100)] string? LastName,
    [Required, EmailAddress, StringLength(320)] string Email,
    [StringLength(128)] string? CurrentPassword);
public sealed record ChangeAccountPasswordRequest(
    [Required, StringLength(128)] string CurrentPassword,
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword);
public sealed record DeleteAccountRequest([Required, StringLength(128)] string CurrentPassword, bool Confirmed);
public sealed record AccountPassenger(int Number, string Name, string? Pnr, string? TicketGuid);
public sealed record AccountReservation(Guid Id, string TicketCode, string TourName, DateOnly TourDate,
    TimeOnly DepartureTime, string DeparturePort, int GuestCount, decimal Amount, string Currency,
    string Status, string PaymentStatus, string TicketingStatus, string? EmailStatus, string? CancellationStatus, IReadOnlyList<AccountPassenger> Passengers,
    string? CancellationEmailStatus = null, DateTime? CancellationEmailSentAtUtc = null);
