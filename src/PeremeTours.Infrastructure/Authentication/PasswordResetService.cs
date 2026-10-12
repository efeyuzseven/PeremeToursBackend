using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Authentication;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Authentication;

internal interface IPasswordResetEmailSender
{
    Task SendAsync(string email, string token, string language, CancellationToken cancellationToken);
}

internal sealed class PasswordResetService(PeremeToursDbContext db,
    IPasswordHasher<UserAccount> hasher, IPasswordResetEmailSender sender,
    IOptions<MailOptions> mail, TimeProvider clock) : IPasswordResetService
{
    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task RequestAsync(PasswordResetRequest request, CancellationToken cancellationToken)
    {
        if (!mail.Value.Enabled) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var account = await db.Users.SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail
            && user.IsActive, cancellationToken);
        if (account is null) return;
        var since = now.AddHours(-1);
        var requests = await db.PasswordResetTokens.AsNoTracking()
            .Where(item => item.UserId == account.Id && item.CreatedAtUtc >= since)
            .Select(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        if (requests.Count >= 3 || requests.Any(created => created > now.AddMinutes(-1))) return;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(), UserId = account.Id, User = account, TokenHash = HashToken(token),
            AuthenticationStamp = account.AuthenticationStamp, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(30),
        });
        await db.SaveChangesAsync(cancellationToken);
        // Raw tokens exist only for this SMTP delivery, never in database columns or log messages.
        await sender.SendAsync(account.Email, token, request.Language, cancellationToken);
    }

    public async Task<bool> ResetAsync(string token, string password, CancellationToken cancellationToken)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit) || password.Length > 128 || !PasswordPolicy.IsValid(password))
            return false;
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = HashToken(token);
        var reset = await db.PasswordResetTokens.AsNoTracking().Include(item => item.User)
            .SingleOrDefaultAsync(item => item.TokenHash == hash && item.ConsumedAtUtc == null
                && item.ExpiresAtUtc > now && item.User.IsActive, cancellationToken);
        if (reset is null || reset.AuthenticationStamp != reset.User.AuthenticationStamp) return false;
        var passwordHash = hasher.HashPassword(reset.User, password);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stamp = Guid.NewGuid();
        var updated = await db.Users.Where(user => user.Id == reset.UserId && user.IsActive
                && user.AuthenticationStamp == reset.AuthenticationStamp)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, passwordHash)
                .SetProperty(user => user.AuthenticationStamp, stamp)
                .SetProperty(user => user.UpdatedAtUtc, clock.GetUtcNow()), cancellationToken);
        if (updated != 1) return false;
        var claimed = await db.PasswordResetTokens.Where(item => item.Id == reset.Id
                && item.ConsumedAtUtc == null && item.ExpiresAtUtc > clock.GetUtcNow().UtcDateTime)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConsumedAtUtc, now), cancellationToken);
        if (claimed != 1) return false;
        await db.PasswordResetTokens.Where(item => item.UserId == reset.UserId && item.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConsumedAtUtc, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
