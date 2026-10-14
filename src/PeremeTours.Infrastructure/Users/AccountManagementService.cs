using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PeremeTours.Application.Authentication;
using PeremeTours.Application.Users;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Users;

internal sealed class AccountManagementService(PeremeToursDbContext db,
    IPasswordHasher<UserAccount> hasher, IConfiguration configuration, TimeProvider clock) : IAccountManagementService
{
    private readonly string _bootstrapEmail = (configuration["DeploymentBootstrap:AdminEmail"]
        ?? "admin@peremetours.com").Trim().ToUpperInvariant();

    public async Task<AccountDetails?> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            user => user.Id == userId && user.IsActive && !user.IsDeleted, cancellationToken);
        if (account is null) return null;
        var protectedAccount = account.NormalizedEmail == _bootstrapEmail;
        var lastAdmin = account.Role == UserRoles.Admin && !await db.Users.AnyAsync(
            user => user.Id != userId && user.IsActive && !user.IsDeleted && user.Role == UserRoles.Admin, cancellationToken);
        return new(account.FirstName, account.LastName, account.Email, account.CreatedAtUtc,
            !protectedAccount, !protectedAccount && !lastAdmin,
            protectedAccount ? "ACCOUNT_DELETION_PROTECTED" : lastAdmin ? "LAST_ADMINISTRATOR" : null);
    }

    public Task<AccountOperationResult> UpdateProfileAsync(Guid userId, string? authenticationStamp,
        UpdateAccountCommand command, CancellationToken cancellationToken)
    {
        var firstName = command.FirstName.Trim();
        var lastName = command.LastName?.Trim();
        var email = command.Email.Trim().ToLowerInvariant();
        if (firstName.Length is < 1 or > 100 || lastName?.Length > 100 || email.Length is < 1 or > 320
            || !new EmailAddressAttribute().IsValid(email))
            return Task.FromResult(new AccountOperationResult(AccountOperationStatus.InvalidProfile));
        return RunAsync(userId, authenticationStamp, async (account, token) =>
        {
            var normalized = email.ToUpperInvariant();
            var emailChanged = normalized != account.NormalizedEmail;
            if (emailChanged)
            {
                if (account.NormalizedEmail == _bootstrapEmail) return new(AccountOperationStatus.EmailProtected);
                if (!Verify(account, command.CurrentPassword)) return new(AccountOperationStatus.PasswordIncorrect);
                if (await db.Users.AnyAsync(user => user.Id != userId && user.NormalizedEmail == normalized, token))
                    return new(AccountOperationStatus.EmailAlreadyExists);
            }
            var now = clock.GetUtcNow();
            var stamp = emailChanged ? Guid.NewGuid() : account.AuthenticationStamp;
            var changed = await Unchanged(account).ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.FirstName, firstName)
                .SetProperty(user => user.LastName, string.IsNullOrEmpty(lastName) ? null : lastName)
                .SetProperty(user => user.Email, email).SetProperty(user => user.NormalizedEmail, normalized)
                .SetProperty(user => user.AuthenticationStamp, stamp).SetProperty(user => user.UpdatedAtUtc, now), token);
            if (changed != 1) return new(AccountOperationStatus.Conflict);
            if (emailChanged) await RevokeResetLinksAsync(userId, token);
            return new(AccountOperationStatus.Updated, emailChanged);
        }, cancellationToken);
    }

    public Task<AccountOperationResult> ChangePasswordAsync(Guid userId, string? authenticationStamp,
        string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        if (newPassword.Length > 128 || !PasswordPolicy.IsValid(newPassword))
            return Task.FromResult(new AccountOperationResult(AccountOperationStatus.InvalidPassword));
        return RunAsync(userId, authenticationStamp, async (account, token) =>
        {
            if (!Verify(account, currentPassword)) return new(AccountOperationStatus.PasswordIncorrect);
            var hash = hasher.HashPassword(account, newPassword);
            var stamp = Guid.NewGuid();
            var now = clock.GetUtcNow();
            var changed = await Unchanged(account).ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.PasswordHash, hash).SetProperty(user => user.AuthenticationStamp, stamp)
                .SetProperty(user => user.UpdatedAtUtc, now), token);
            if (changed != 1) return new(AccountOperationStatus.Conflict);
            await RevokeResetLinksAsync(userId, token);
            return new(AccountOperationStatus.Updated, true);
        }, cancellationToken);
    }

    public Task<AccountOperationResult> DeleteAccountAsync(Guid userId, string? authenticationStamp,
        string currentPassword, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed) return Task.FromResult(new AccountOperationResult(AccountOperationStatus.ConfirmationRequired));
        return RunAsync(userId, authenticationStamp, async (account, token) =>
        {
            if (account.NormalizedEmail == _bootstrapEmail) return new(AccountOperationStatus.DeletionProtected);
            if (account.Role == UserRoles.Admin && !await db.Users.AnyAsync(user => user.Id != userId
                && user.IsActive && !user.IsDeleted && user.Role == UserRoles.Admin, token))
                return new(AccountOperationStatus.LastAdministrator);
            if (!Verify(account, currentPassword)) return new(AccountOperationStatus.PasswordIncorrect);
            var tombstoneEmail = $"deleted-{Guid.NewGuid():N}@deleted.invalid";
            var normalized = tombstoneEmail.ToUpperInvariant();
            var now = clock.GetUtcNow();
            var stamp = Guid.NewGuid();
            // Never delete or reassign tickets, passenger snapshots, payments, outboxes or audit records.
            var changed = await Unchanged(account).ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.IsDeleted, true).SetProperty(user => user.IsActive, false)
                .SetProperty(user => user.DeletedAtUtc, now).SetProperty(user => user.UpdatedAtUtc, now)
                .SetProperty(user => user.FirstName, "Silinmiş kullanıcı").SetProperty(user => user.LastName, (string?)null)
                .SetProperty(user => user.Email, tombstoneEmail).SetProperty(user => user.NormalizedEmail, normalized)
                .SetProperty(user => user.PasswordHash, string.Empty).SetProperty(user => user.Role, UserRoles.User)
                .SetProperty(user => user.AuthenticationStamp, stamp), token);
            if (changed != 1) return new(AccountOperationStatus.Conflict);
            await RevokeResetLinksAsync(userId, token);
            return new(AccountOperationStatus.Updated, true);
        }, cancellationToken);
    }

    private bool Verify(UserAccount account, string? password) => !string.IsNullOrEmpty(password)
        && password.Length <= 128 && hasher.VerifyHashedPassword(account, account.PasswordHash, password)
        != PasswordVerificationResult.Failed;

    private IQueryable<UserAccount> Unchanged(UserAccount account) => db.Users.Where(user => user.Id == account.Id
        && user.IsActive && !user.IsDeleted && user.AuthenticationStamp == account.AuthenticationStamp
        && user.PasswordHash == account.PasswordHash && user.UpdatedAtUtc == account.UpdatedAtUtc);

    private Task<int> RevokeResetLinksAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.PasswordResetTokens.Where(token => token.UserId == userId && token.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.ConsumedAtUtc, now), cancellationToken);
    }

    private async Task<AccountOperationResult> RunAsync(Guid userId, string? authenticationStamp,
        Func<UserAccount, CancellationToken, Task<AccountOperationResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId
                && user.IsActive && !user.IsDeleted, cancellationToken);
            // Check again inside the transaction; middleware validation alone cannot protect against an intervening reset.
            if (account is null || account.AuthenticationStamp?.ToString("N") != authenticationStamp)
                return new(AccountOperationStatus.Unauthorized);
            var result = await operation(account, cancellationToken);
            if (result.Status == AccountOperationStatus.Updated) await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Users_NormalizedEmail" })
        { return new(AccountOperationStatus.EmailAlreadyExists); }
        catch (PostgresException exception) when (exception is
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Users_NormalizedEmail" })
        { return new(AccountOperationStatus.EmailAlreadyExists); }
        catch (Exception exception) when (exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
            || exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } })
        { return new(AccountOperationStatus.Conflict); }
    }
}
