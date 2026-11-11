using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Tickets;

internal sealed record TicketCancellationActor(string Name, string Email);

// Resolves the staff users who started cancellations/refunds, for the admin ticket lists.
internal static class TicketCancellationActors
{
    public static async Task<IReadOnlyDictionary<Guid, TicketCancellationActor>> LoadAsync(PeremeToursDbContext db,
        IEnumerable<Guid> actorIds, CancellationToken cancellationToken)
    {
        var ids = actorIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, TicketCancellationActor>();
        var users = await db.Users.AsNoTracking().Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.FirstName, user.LastName, user.Email }).ToListAsync(cancellationToken);
        return users.ToDictionary(user => user.Id,
            user => new TicketCancellationActor($"{user.FirstName} {user.LastName}".Trim(), user.Email));
    }

    public static TicketCancellationDetails? Details(TicketCancellation? cancellation, IReadOnlyDictionary<Guid, TicketCancellationActor>? actors)
    {
        if (cancellation is null) return null;
        var actor = actors?.GetValueOrDefault(cancellation.ActorUserId);
        return new(actor?.Name, actor?.Email, cancellation.Reason, cancellation.RequestedAtUtc, cancellation.CompletedAtUtc);
    }
}
