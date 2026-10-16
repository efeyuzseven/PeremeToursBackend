using PeremeTours.Domain.Users;

namespace PeremeTours.UnitTests;

public sealed class UserRolesTests
{
    [Theory]
    [InlineData(UserRoles.Admin, true)]
    [InlineData(UserRoles.User, true)]
    [InlineData(UserRoles.Editor, true)]
    [InlineData(UserRoles.Advisor, true)]
    [InlineData(UserRoles.Service, true)]
    [InlineData("editor", false)]
    [InlineData("advisor", false)]
    [InlineData("service", false)]
    [InlineData("Operator", false)]
    [InlineData("admin", false)]
    public void IsValidAcceptsOnlyKnownRoles(string role, bool expected)
    {
        Assert.Equal(expected, UserRoles.IsValid(role));
    }
}
