namespace PeremeTours.Application.Users;

public sealed record UserSummary(
    Guid Id,
    string Email,
    string FirstName,
    string? LastName,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    bool CanEditEmail = true
);

public sealed record UpdateUserCommand(
    string? Role,
    bool? IsActive,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null
);

public enum UpdateUserStatus
{
    Updated,
    NotFound,
    InvalidRole,
    SelfLockout,
    InvalidProfile,
    EmailAlreadyExists,
    EmailChangeProtected,
    Forbidden,
    Conflict,
}

public interface IUserAdminService
{
    Task<IReadOnlyList<UserSummary>> ListAsync(
        CancellationToken cancellationToken
    );

    Task<UpdateUserStatus> UpdateAsync(
        Guid actorUserId,
        Guid userId,
        UpdateUserCommand command,
        CancellationToken cancellationToken
    );
}
