namespace PeremeTours.Domain.Users;

public sealed class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required UserAccount User { get; set; }
    public required string TokenHash { get; set; }
    public Guid? AuthenticationStamp { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}
