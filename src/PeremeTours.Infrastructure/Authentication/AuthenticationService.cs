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
            AuthenticationStamp = Guid.NewGuid(),
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
        if (account is null || !account.IsActive || account.IsDeleted)
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
            var originalHash = account.PasswordHash;
            var replacementHash = passwordHasher.HashPassword(
                account,
                command.Password
            );
            var now = DateTimeOffset.UtcNow;
            // A simultaneous reset must not be overwritten by a login's legacy-hash upgrade.
            var updated = await dbContext.Users.Where(user => user.Id == account.Id && user.IsActive && !user.IsDeleted
                    && user.PasswordHash == originalHash && user.AuthenticationStamp == account.AuthenticationStamp)
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, replacementHash)
                    .SetProperty(user => user.UpdatedAtUtc, now), cancellationToken);
            if (updated != 1) return null;
            account.PasswordHash = replacementHash;
            account.UpdatedAtUtc = now;
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
            .Where(user => user.Id == userId && user.IsActive && !user.IsDeleted)
            .Select(user => new AuthenticatedUser(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Role,
                user.AuthenticationStamp
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
            account.Role,
            account.AuthenticationStamp
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
