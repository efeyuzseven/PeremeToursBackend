using System.Text;
using PeremeTours.Application.Payments;

namespace PeremeTours.Infrastructure.Payments;

internal static class ZiraatResponseReader
{
    static ZiraatResponseReader()
    {
        // The live CC5 endpoint returns text/xml;charset=ISO-8859-9.
        // These legacy Turkish encodings are not available in .NET by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<string> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            // Honour the actual Content-Type charset; do not blindly decode bank data as UTF-8.
            return await content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Do not expose bank response bodies or parser details in diagnostics.
            throw new PaymentGatewayException("Banka yanıtının karakter kodlaması doğrulanamadı.");
        }
    }
}
