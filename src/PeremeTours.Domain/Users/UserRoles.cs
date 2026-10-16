namespace PeremeTours.Domain.Users;

public static class UserRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string Editor = "Editor";
    public const string Advisor = "Advisor";
    public const string Service = "Service";

    public const string ContentManagers = Admin + "," + Editor + "," + Advisor;
    public const string CustomerSupport = Admin + "," + Service;

    public static bool IsValid(string role) =>
        role is Admin or User or Editor or Advisor or Service;
}
