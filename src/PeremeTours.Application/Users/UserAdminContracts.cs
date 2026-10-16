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

public sealed record CreateUserCommand(string FirstName, string? LastName, string Email, string Password, string Role);
public enum CreateUserStatus { Created, Forbidden, InvalidRole, InvalidProfile, InvalidPassword, EmailAlreadyExists, Conflict }
public sealed record CreateUserResult(CreateUserStatus Status, UserSummary? User = null);

public interface IUserAdminService
{
    Task<CreateUserResult> CreateAsync(Guid actorUserId, string? authenticationStamp,
        CreateUserCommand command, CancellationToken cancellationToken);

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
