namespace PeremeTours.Application.Authentication;

public sealed record RegisterCommand(
    string Email,
    string Password,
    string FirstName,
    string? LastName
);

public sealed record LoginCommand(
    string Email,
    string Password
);

public sealed record AuthenticatedUser(
    Guid Id,
    string Email,
    string FirstName,
    string? LastName,
    string Role
);

public sealed record AuthenticationResult(
    AuthenticatedUser User,
    string AccessToken,
    DateTimeOffset ExpiresAtUtc
);

public enum RegistrationStatus
{
    Created,
    EmailAlreadyExists,
}

public sealed record RegistrationResult(
    RegistrationStatus Status,
    AuthenticationResult? Authentication
);

public interface IAuthenticationService
{
    Task<RegistrationResult> RegisterAsync(
        RegisterCommand command,
        CancellationToken cancellationToken
    );

    Task<AuthenticationResult?> LoginAsync(
        LoginCommand command,
        CancellationToken cancellationToken
    );

    Task<AuthenticatedUser?> GetUserAsync(
        Guid userId,
        CancellationToken cancellationToken
    );
}

public interface IAccessTokenService
{
    (string Token, DateTimeOffset ExpiresAtUtc) Create(
        AuthenticatedUser user
    );
}
