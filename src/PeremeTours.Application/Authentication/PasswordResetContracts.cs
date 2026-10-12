namespace PeremeTours.Application.Authentication;

public sealed record PasswordResetRequest(string Email, string Language);

public interface IPasswordResetRequests
{
    bool TryEnqueue(PasswordResetRequest request);
}

public interface IPasswordResetService
{
    Task<bool> ResetAsync(string token, string password, CancellationToken cancellationToken);
}
