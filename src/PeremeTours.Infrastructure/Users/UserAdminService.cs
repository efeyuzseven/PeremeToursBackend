using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Users;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Users;

internal sealed class UserAdminService(PeremeToursDbContext dbContext)
    : IUserAdminService
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        return await dbContext.Users
            .AsNoTracking()
            .OrderByDescending(user => user.CreatedAtUtc)
            .Select(user => new UserSummary(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Role,
                user.IsActive,
                user.CreatedAtUtc
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
        var account = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == userId,
            cancellationToken
        );
        if (account is null)
        {
            return UpdateUserStatus.NotFound;
        }
        if (command.Role is not null && !UserRoles.IsValid(command.Role))
        {
            return UpdateUserStatus.InvalidRole;
        }
        if (
            actorUserId == userId
            && (command.IsActive == false || command.Role == UserRoles.User)
        )
        {
            return UpdateUserStatus.SelfLockout;
        }

        if (command.Role is not null)
        {
            account.Role = command.Role;
        }
        if (command.IsActive.HasValue)
        {
            account.IsActive = command.IsActive.Value;
        }
        account.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return UpdateUserStatus.Updated;
    }
}
