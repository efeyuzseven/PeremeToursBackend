using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Users;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController(IUserAdminService userAdminService)
    : ControllerBase
{
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
                Detail = "Rol Admin veya User olmalıdır.",
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
