namespace PeremeTours.Domain.Users;

public sealed class UserAccount
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string NormalizedEmail { get; set; }

    public required string FirstName { get; set; }

    public string? LastName { get; set; }

    public required string PasswordHash { get; set; }

    // Null preserves existing sessions until the account's first password reset.
    public Guid? AuthenticationStamp { get; set; }

    public required string Role { get; set; }

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
