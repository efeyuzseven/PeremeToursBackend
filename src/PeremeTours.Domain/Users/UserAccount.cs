namespace PeremeTours.Domain.Users;

public sealed class UserAccount
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string NormalizedEmail { get; set; }

    public required string FirstName { get; set; }

    public string? LastName { get; set; }

    public required string PasswordHash { get; set; }

    // Null preserves legacy sessions until the account's first credential/access change.
    public Guid? AuthenticationStamp { get; set; }

    public required string Role { get; set; }

    public bool IsActive { get; set; }

    // A tombstone preserves financial records and administrator audit foreign keys without a reusable login.
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
