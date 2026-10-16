using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PeremeTours.Application.Users;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Users;

internal sealed class UserAdminService(PeremeToursDbContext dbContext, IConfiguration configuration)
    : IUserAdminService
{
    private readonly string _bootstrapEmail = (configuration["DeploymentBootstrap:AdminEmail"]
        ?? "admin@peremetours.com").Trim().ToUpperInvariant();
    public async Task<IReadOnlyList<UserSummary>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        return await dbContext.Users
            .AsNoTracking()
            .Where(user => !user.IsDeleted)
            .OrderByDescending(user => user.CreatedAtUtc)
            .Select(user => new UserSummary(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Role,
                user.IsActive,
                user.CreatedAtUtc,
                user.NormalizedEmail != _bootstrapEmail
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<UpdateUserStatus> UpdateAsync(
        Guid actorUserId,
        Guid userId,
        UpdateUserCommand command,
        CancellationToken cancellationToken
    )
    {
        if (command.Role is not null && !UserRoles.IsValid(command.Role)) return UpdateUserStatus.InvalidRole;
        var firstName = command.FirstName?.Trim();
        var lastName = command.LastName?.Trim();
        var email = command.Email?.Trim().ToLowerInvariant();
        if ((firstName is not null && (firstName.Length is < 1 or > 100))
            || (lastName is not null && lastName.Length > 100)
            || (email is not null && (email.Length is < 1 or > 320 || !new EmailAddressAttribute().IsValid(email))))
            return UpdateUserStatus.InvalidProfile;
        try
        {
            // Serialize permission checks and updates; races must not partially edit a profile or remove both administrators.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (!await dbContext.Users.AnyAsync(user => user.Id == actorUserId && user.IsActive && !user.IsDeleted && user.Role == UserRoles.Admin, cancellationToken))
                return UpdateUserStatus.Forbidden;
            var account = await dbContext.Users.SingleOrDefaultAsync(
                user => user.Id == userId && !user.IsDeleted,
                cancellationToken
            );
            if (account is null)
            {
                return UpdateUserStatus.NotFound;
            }
            if (
                actorUserId == userId
                && (command.IsActive == false || (command.Role is not null && command.Role != UserRoles.Admin))
            )
            {
                return UpdateUserStatus.SelfLockout;
            }

            var normalizedEmail = email?.ToUpperInvariant();
            var emailChanged = normalizedEmail is not null && normalizedEmail != account.NormalizedEmail;
            if (emailChanged && account.NormalizedEmail == _bootstrapEmail)
                return UpdateUserStatus.EmailChangeProtected;
            if (emailChanged && await dbContext.Users.AnyAsync(user => user.Id != userId && user.NormalizedEmail == normalizedEmail, cancellationToken))
                return UpdateUserStatus.EmailAlreadyExists;
            var accessChanged = emailChanged || (command.Role is not null && command.Role != account.Role)
                || (command.IsActive.HasValue && command.IsActive.Value != account.IsActive);
            if (firstName is not null) account.FirstName = firstName;
            if (lastName is not null) account.LastName = lastName.Length == 0 ? null : lastName;
            if (email is not null) { account.Email = email; account.NormalizedEmail = normalizedEmail!; }

            if (command.Role is not null)
            {
                account.Role = command.Role;
            }
            if (command.IsActive.HasValue)
            {
                account.IsActive = command.IsActive.Value;
            }
            account.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (accessChanged)
            {
                account.AuthenticationStamp = Guid.NewGuid();
                var consumedAt = DateTime.UtcNow;
                await dbContext.PasswordResetTokens.Where(item => item.UserId == userId && item.ConsumedAtUtc == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConsumedAtUtc, consumedAt), cancellationToken);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return UpdateUserStatus.Updated;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Users_NormalizedEmail" })
        { return UpdateUserStatus.EmailAlreadyExists; }
        catch (Exception exception) when (exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
            || exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } })
        { return UpdateUserStatus.Conflict; }
    }
}
