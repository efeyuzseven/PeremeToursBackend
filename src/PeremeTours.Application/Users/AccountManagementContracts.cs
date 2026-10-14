namespace PeremeTours.Application.Users;

public sealed record AccountDetails(string FirstName, string? LastName, string Email,
    DateTimeOffset CreatedAtUtc, bool CanEditEmail, bool CanDeleteAccount, string? DeletionBlockedReason);
public sealed record UpdateAccountCommand(string FirstName, string? LastName, string Email, string? CurrentPassword);
public enum AccountOperationStatus
{
    Updated, Unauthorized, InvalidProfile, InvalidPassword, PasswordIncorrect,
    EmailAlreadyExists, EmailProtected, DeletionProtected, LastAdministrator, ConfirmationRequired, Conflict,
}
public sealed record AccountOperationResult(AccountOperationStatus Status, bool RequiresLogin = false);
public interface IAccountManagementService
{
    Task<AccountDetails?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
    Task<AccountOperationResult> UpdateProfileAsync(Guid userId, string? authenticationStamp,
        UpdateAccountCommand command, CancellationToken cancellationToken);
    Task<AccountOperationResult> ChangePasswordAsync(Guid userId, string? authenticationStamp,
        string currentPassword, string newPassword, CancellationToken cancellationToken);
    Task<AccountOperationResult> DeleteAccountAsync(Guid userId, string? authenticationStamp,
        string currentPassword, bool confirmed, CancellationToken cancellationToken);
}
