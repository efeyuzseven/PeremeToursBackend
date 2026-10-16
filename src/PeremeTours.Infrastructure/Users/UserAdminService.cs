using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using PeremeTours.Application.Users;
using PeremeTours.Application.Authentication;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Users;

internal sealed partial class UserAdminService(PeremeToursDbContext dbContext, IConfiguration configuration,
    IPasswordHasher<UserAccount> passwordHasher, ILogger<UserAdminService> logger)
    : IUserAdminService
{
    private readonly string _bootstrapEmail = (configuration["DeploymentBootstrap:AdminEmail"]
        ?? "admin@peremetours.com").Trim().ToUpperInvariant();

    public async Task<CreateUserResult> CreateAsync(Guid actorUserId, string? authenticationStamp,
        CreateUserCommand command, CancellationToken cancellationToken)
    {
        if (!UserRoles.IsValid(command.Role)) return new(CreateUserStatus.InvalidRole);
        var firstName = command.FirstName.Trim();
        var lastName = command.LastName?.Trim();
        var email = command.Email.Trim().ToLowerInvariant();
        if (firstName.Length is < 1 or > 100 || lastName?.Length > 100 || email.Length is < 1 or > 320
            || !new EmailAddressAttribute().IsValid(email)) return new(CreateUserStatus.InvalidProfile);
        if (command.Password.Length > 128 || !PasswordPolicy.IsValid(command.Password)) return new(CreateUserStatus.InvalidPassword);
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var actor = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == actorUserId
                && user.IsActive && !user.IsDeleted && user.Role == UserRoles.Admin, cancellationToken);
            if (actor is null || actor.AuthenticationStamp?.ToString("N") != authenticationStamp) return new(CreateUserStatus.Forbidden);
            var normalizedEmail = email.ToUpperInvariant();
            // Existing or inactive accounts must never be overwritten, promoted or given a new password by this operation.
            if (await dbContext.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken))
                return new(CreateUserStatus.EmailAlreadyExists);
            var now = DateTimeOffset.UtcNow;
            var account = new UserAccount { Id = Guid.NewGuid(), FirstName = firstName,
                LastName = string.IsNullOrEmpty(lastName) ? null : lastName, Email = email, NormalizedEmail = normalizedEmail,
                PasswordHash = string.Empty, AuthenticationStamp = Guid.NewGuid(), Role = command.Role, IsActive = true,
                CreatedAtUtc = now, UpdatedAtUtc = now };
            account.PasswordHash = passwordHasher.HashPassword(account, command.Password);
            dbContext.Users.Add(account);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogUserCreated(logger, actorUserId, account.Id, account.Role);
            // Return only the public management DTO. Never return a login token or any password material.
            return new(CreateUserStatus.Created, new(account.Id, account.Email, account.FirstName, account.LastName,
                account.Role, account.IsActive, account.CreatedAtUtc, account.NormalizedEmail != _bootstrapEmail));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Users_NormalizedEmail" })
        { return new(CreateUserStatus.EmailAlreadyExists); }
        catch (Exception exception) when (exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
            || exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } })
        { return new(CreateUserStatus.Conflict); }
    }

    [LoggerMessage(EventId = 1601, Level = LogLevel.Information, Message = "Administrator {ActorUserId} created account {UserId} with role {Role}.")]
    private static partial void LogUserCreated(ILogger logger, Guid actorUserId, Guid userId, string role);
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
