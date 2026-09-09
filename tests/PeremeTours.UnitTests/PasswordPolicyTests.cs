using PeremeTours.Application.Authentication;

namespace PeremeTours.UnitTests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("StrongPass1", true)]
    [InlineData("short1A", false)]
    [InlineData("alllowercase1", false)]
    [InlineData("ALLUPPERCASE1", false)]
    [InlineData("NoNumbersHere", false)]
    public void IsValidEnforcesRequiredComplexity(
        string password,
        bool expected
    )
    {
        Assert.Equal(expected, PasswordPolicy.IsValid(password));
    }
}
