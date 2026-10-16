using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PeremeTours.Application.Users;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController(IUserAdminService userAdminService)
    : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting("user-create")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<UserSummary>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UserSummary>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)) return Forbid();
        var result = await userAdminService.CreateAsync(actorId, User.FindFirstValue("auth_stamp"),
            new(request.FirstName, request.LastName, request.Email, request.Password, request.Role), cancellationToken);
        return result.Status switch
        {
            CreateUserStatus.Created => StatusCode(StatusCodes.Status201Created, result.User),
            CreateUserStatus.Forbidden => Forbid(),
            CreateUserStatus.InvalidRole => Problem(statusCode: 400, title: "USER_ROLE_INVALID"),
            CreateUserStatus.InvalidProfile => Problem(statusCode: 400, title: "USER_PROFILE_INVALID"),
            CreateUserStatus.InvalidPassword => Problem(statusCode: 400, title: "PASSWORD_POLICY_INVALID"),
            CreateUserStatus.EmailAlreadyExists => Problem(statusCode: 409, title: "USER_EMAIL_ALREADY_EXISTS"),
            _ => Problem(statusCode: 409, title: "USER_CREATE_CONFLICT"),
        };
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UserSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> List(
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await userAdminService.ListAsync(cancellationToken));
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        UpdateUserRequest request,
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)) return Forbid();
        var result = await userAdminService.UpdateAsync(
            actorId,
            id,
            new UpdateUserCommand(request.Role, request.IsActive, request.FirstName, request.LastName, request.Email),
            cancellationToken
        );
        return result switch
        {
            UpdateUserStatus.Updated => NoContent(),
            UpdateUserStatus.NotFound => NotFound(),
            UpdateUserStatus.Forbidden => Forbid(),
            UpdateUserStatus.InvalidProfile => Problem(statusCode: 400, title: "USER_PROFILE_INVALID",
                detail: "Ad ve e-posta geçerli olmalı; ad/soyad en fazla 100 karakter olabilir."),
            UpdateUserStatus.EmailAlreadyExists => Problem(statusCode: 409, title: "USER_EMAIL_ALREADY_EXISTS",
                detail: "Bu e-posta adresi başka bir hesap tarafından kullanılıyor."),
            UpdateUserStatus.EmailChangeProtected => Problem(statusCode: 400, title: "USER_EMAIL_PROTECTED",
                detail: "Varsayılan yönetici hesabının giriş e-postası dağıtım ayarlarına bağlıdır; bu ekrandan değiştirilemez."),
            UpdateUserStatus.Conflict => Problem(statusCode: 409, title: "USER_UPDATE_CONFLICT",
                detail: "Kullanıcı başka bir işlem tarafından değiştirildi. Listeyi yenileyip tekrar deneyin."),
            UpdateUserStatus.InvalidRole => BadRequest(new ProblemDetails
            {
                Title = "Geçersiz rol",
                Detail = "Rol Admin, User, Editor, Advisor veya Service olmalıdır.",
                Status = StatusCodes.Status400BadRequest,
            }),
            UpdateUserStatus.SelfLockout => BadRequest(new ProblemDetails
            {
                Title = "İşlem engellendi",
                Detail = "Kendi admin yetkinizi veya hesabınızı kapatamazsınız.",
                Status = StatusCodes.Status400BadRequest,
            }),
            _ => throw new InvalidOperationException("Unknown user update status."),
        };
    }
}

public sealed class UpdateUserRequest
{
    [StringLength(100, MinimumLength = 1)]
    public string? FirstName { get; init; }

    [StringLength(100)]
    public string? LastName { get; init; }

    [EmailAddress, StringLength(320, MinimumLength = 1)]
    public string? Email { get; init; }

    [StringLength(32)]
    public string? Role { get; init; }

    public bool? IsActive { get; init; }
}

public sealed class CreateUserRequest
{
    [Required, StringLength(100)] public required string FirstName { get; init; }
    [StringLength(100)] public string? LastName { get; init; }
    [Required, EmailAddress, StringLength(320)] public required string Email { get; init; }
    [Required, StringLength(128, MinimumLength = 8)] public required string Password { get; init; }
    [Required, StringLength(32)] public required string Role { get; init; }
}
