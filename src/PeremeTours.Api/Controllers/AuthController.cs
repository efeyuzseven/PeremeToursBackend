using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Authentication;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IAuthenticationService authenticationService)
    : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponse>> Register(
        RegisterRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!PasswordPolicy.IsValid(request.Password))
        {
            ModelState.AddModelError(
                nameof(request.Password),
                "Şifre en az 8 karakter olmalı; büyük harf, küçük harf ve rakam içermelidir."
            );
            return ValidationProblem(ModelState);
        }

        var result = await authenticationService.RegisterAsync(
            new RegisterCommand(
                request.Email,
                request.Password,
                request.FirstName,
                request.LastName
            ),
            cancellationToken
        );
        if (result.Status == RegistrationStatus.EmailAlreadyExists)
        {
            return Conflict(CreateProblem(
                StatusCodes.Status409Conflict,
                "E-posta zaten kayıtlı",
                "Bu e-posta adresiyle daha önce bir hesap oluşturulmuş."
            ));
        }

        return StatusCode(
            StatusCodes.Status201Created,
            Map(result.Authentication!)
        );
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await authenticationService.LoginAsync(
            new LoginCommand(request.Email, request.Password),
            cancellationToken
        );
        if (result is null)
        {
            return Unauthorized(CreateProblem(
                StatusCodes.Status401Unauthorized,
                "Giriş başarısız",
                "E-posta adresi veya şifre hatalı."
            ));
        }
        return Ok(Map(result));
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserResponse>> Me(
        CancellationToken cancellationToken
    )
    {
        var identifier = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(identifier, out var userId))
        {
            return Unauthorized();
        }
        var user = await authenticationService.GetUserAsync(
            userId,
            cancellationToken
        );
        return user is null ? Unauthorized() : Ok(Map(user));
    }

    private ProblemDetails CreateProblem(int status, string title, string detail) =>
        new()
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = HttpContext.Request.Path,
        };

    private static AuthResponse Map(AuthenticationResult authentication) =>
        new(
            Map(authentication.User),
            authentication.AccessToken,
            authentication.ExpiresAtUtc
        );

    private static UserResponse Map(AuthenticatedUser user) =>
        new(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.Role
        );
}

public sealed class RegisterRequest
{
    [Required, EmailAddress, StringLength(320)]
    public required string Email { get; init; }

    [Required, StringLength(100, MinimumLength = 1)]
    public required string FirstName { get; init; }

    [StringLength(100)]
    public string? LastName { get; init; }

    [Required, StringLength(128, MinimumLength = 8)]
    public required string Password { get; init; }
}

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(320)]
    public required string Email { get; init; }

    [Required, StringLength(128, MinimumLength = 8)]
    public required string Password { get; init; }
}

public sealed record AuthResponse(
    UserResponse User,
    string AccessToken,
    DateTimeOffset ExpiresAtUtc
);

public sealed record UserResponse(
    Guid Id,
    string Email,
    string FirstName,
    string? LastName,
    string Role
);
