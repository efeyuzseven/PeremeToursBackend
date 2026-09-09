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
    ) => Ok(await userAdminService.ListAsync(cancellationToken));

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
        var actorId = Guid.Parse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!
        );
        var result = await userAdminService.UpdateAsync(
            actorId,
            id,
            new UpdateUserCommand(request.Role, request.IsActive),
            cancellationToken
        );
        return result switch
        {
            UpdateUserStatus.Updated => NoContent(),
            UpdateUserStatus.NotFound => NotFound(),
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
    [StringLength(32)]
    public string? Role { get; init; }

    public bool? IsActive { get; init; }
}
