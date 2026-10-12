using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.UnitTests;

public sealed class PaymentFrontendOriginsTests
{
    [Fact]
    public void SingleLegacyOriginRemainsSupported() => Assert.Equal(
        ["https://d2bmjk2h6qp4lz.cloudfront.net"],
        new ZiraatPosOptions { FrontendOrigin = "https://d2bmjk2h6qp4lz.cloudfront.net" }.GetFrontendOrigins());

    [Fact]
    public void ProductionAndTestOriginsAreExactNormalizedAndDeduplicated()
    {
        var options = new ZiraatPosOptions
        {
            FrontendOrigin = "https://d2bmjk2h6qp4lz.cloudfront.net",
            AllowedFrontendOrigins = ["https://pereme.com.tr/", "https://www.pereme.com.tr", "https://PEREME.COM.TR"],
        };
        Assert.Equal(["https://d2bmjk2h6qp4lz.cloudfront.net", "https://pereme.com.tr", "https://www.pereme.com.tr"], options.GetFrontendOrigins());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.pereme.com.tr")]
    [InlineData("http://pereme.com.tr")]
    [InlineData("https://username:password@pereme.com.tr")]
    [InlineData("https://pereme.com.tr/path")]
    [InlineData("https://pereme.com.tr?redirect=evil")]
    [InlineData("https://pereme.com.tr#fragment")]
    [InlineData("javascript:alert(1)")]
    public void InvalidOriginsCannotWeakenFrameOrMessagePolicies(string invalid)
    {
        var options = new ZiraatPosOptions { FrontendOrigin = "https://pereme.com.tr", AllowedFrontendOrigins = [invalid] };
        Assert.Equal(["https://pereme.com.tr"], options.GetFrontendOrigins());
    }
}
