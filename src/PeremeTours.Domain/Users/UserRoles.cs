namespace PeremeTours.Domain.Users;

public static class UserRoles
{
    public const string Admin = "Admin";
    public const string User = "User";

    public static bool IsValid(string role) =>
        role is Admin or User;
}
