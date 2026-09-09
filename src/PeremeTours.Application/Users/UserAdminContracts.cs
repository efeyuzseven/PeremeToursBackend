namespace PeremeTours.Application.Users;

public sealed record UserSummary(
    Guid Id,
    string Email,
    string FirstName,
    string? LastName,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAtUtc
);

public sealed record UpdateUserCommand(
    string? Role,
    bool? IsActive
);

public enum UpdateUserStatus
{
    Updated,
    NotFound,
    InvalidRole,
    SelfLockout,
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
