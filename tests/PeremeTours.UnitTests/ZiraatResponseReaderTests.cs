using System.Net.Http.Headers;
using System.Text;
using PeremeTours.Application.Payments;
using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.UnitTests;

public sealed class ZiraatResponseReaderTests
{
    [Theory]
    [InlineData("ISO-8859-9")]
    [InlineData("windows-1254")]
    public async Task TurkishCharactersAreDecodedUsingTheBankCharset(string charset)
    {
        // These byte values encode "İptal edildi: 350 TL" in both supported Turkish code pages.
        using var content = new ByteArrayContent([0xdd, 0x70, 0x74, 0x61, 0x6c, 0x20, 0x65, 0x64, 0x69, 0x6c, 0x64, 0x69, 0x3a, 0x20, 0x33, 0x35, 0x30, 0x20, 0x54, 0x4c]);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = charset };
        Assert.Equal("İptal edildi: 350 TL", await ZiraatResponseReader.ReadAsync(content, CancellationToken.None));
    }

    [Fact]
    public async Task Utf8RemainsUnchanged()
    {
        using var content = new StringContent("İşlem onaylandı", Encoding.UTF8, "application/xml");
        Assert.Equal("İşlem onaylandı", await ZiraatResponseReader.ReadAsync(content, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownCharsetCannotBecomeAnApprovalOrExposeSensitiveResponseData()
    {
        using var content = new StringContent("fake-password 4111111111111111", Encoding.UTF8, "application/xml");
        content.Headers.ContentType!.CharSet = "not-a-real-encoding";
        var exception = await Assert.ThrowsAsync<PaymentGatewayException>(() => ZiraatResponseReader.ReadAsync(content, CancellationToken.None));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("411111", exception.ToString());
        Assert.DoesNotContain("fake-password", exception.ToString());
    }
}
