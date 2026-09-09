namespace PeremeTours.Application.Authentication;

public static class PasswordPolicy
{
    public static bool IsValid(string password) =>
        password.Length >= 8
        && password.Any(char.IsUpper)
        && password.Any(char.IsLower)
        && password.Any(char.IsDigit);
}
