using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Authentication;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Authentication;

internal sealed class AuthenticationService(
    PeremeToursDbContext dbContext,
    IPasswordHasher<UserAccount> passwordHasher,
    IAccessTokenService accessTokenService
) : IAuthenticationService
{
    public async Task<RegistrationResult> RegisterAsync(
        RegisterCommand command,
        CancellationToken cancellationToken
    )
    {
        var email = command.Email.Trim().ToLowerInvariant();
        var normalizedEmail = NormalizeEmail(email);
        var exists = await dbContext.Users.AnyAsync(
            user => user.NormalizedEmail == normalizedEmail,
            cancellationToken
        );
        if (exists)
        {
            return new RegistrationResult(
                RegistrationStatus.EmailAlreadyExists,
                null
            );
        }

        var now = DateTimeOffset.UtcNow;
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = normalizedEmail,
            FirstName = command.FirstName.Trim(),
            LastName = string.IsNullOrWhiteSpace(command.LastName)
                ? null
                : command.LastName.Trim(),
            PasswordHash = string.Empty,
            Role = UserRoles.User,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        account.PasswordHash = passwordHasher.HashPassword(
            account,
            command.Password
        );
        dbContext.Users.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);

        var authentication = CreateAuthentication(account);
        return new RegistrationResult(
            RegistrationStatus.Created,
            authentication
        );
    }

    public async Task<AuthenticationResult?> LoginAsync(
        LoginCommand command,
        CancellationToken cancellationToken
    )
    {
        var normalizedEmail = NormalizeEmail(command.Email);
        var account = await dbContext.Users.SingleOrDefaultAsync(
            user => user.NormalizedEmail == normalizedEmail,
            cancellationToken
        );
        if (account is null || !account.IsActive)
        {
            return null;
        }

        var result = passwordHasher.VerifyHashedPassword(
            account,
            account.PasswordHash,
            command.Password
        );
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = passwordHasher.HashPassword(
                account,
                command.Password
            );
            account.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return CreateAuthentication(account);
    }

    public async Task<AuthenticatedUser?> GetUserAsync(
        Guid userId,
        CancellationToken cancellationToken
    )
    {
        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.IsActive)
            .Select(user => new AuthenticatedUser(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Role
            ))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private AuthenticationResult CreateAuthentication(UserAccount account)
    {
        var user = new AuthenticatedUser(
            account.Id,
            account.Email,
            account.FirstName,
            account.LastName,
            account.Role
        );
        var token = accessTokenService.Create(user);
        return new AuthenticationResult(
            user,
            token.Token,
            token.ExpiresAtUtc
        );
    }

    private static string NormalizeEmail(string email) =>
        email.Trim().ToUpperInvariant();
}
