using PeremeTours.Domain.Users;

namespace PeremeTours.UnitTests;

public sealed class UserRolesTests
{
    [Theory]
    [InlineData(UserRoles.Admin, true)]
    [InlineData(UserRoles.User, true)]
    [InlineData("Operator", false)]
    [InlineData("admin", false)]
    public void IsValidAcceptsOnlyKnownRoles(string role, bool expected)
    {
        Assert.Equal(expected, UserRoles.IsValid(role));
    }
}
