using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;

namespace PeremeTours.Infrastructure.Payments;

internal sealed record ZiraatPaymentRequest(
    string OrderId,
    decimal Amount,
    string Language,
    PaymentCard Card
);

internal sealed record ZiraatFinalizationResult(
    bool IsApproved,
    string? AuthCode,
    string? HostReference,
    string? ErrorCode,
    string? ErrorMessage
);

internal interface IZiraatPosGateway
{
    Task<string> StartThreeDSecureAsync(
        ZiraatPaymentRequest payment,
        CancellationToken cancellationToken
    );

    bool VerifyCallback(IReadOnlyDictionary<string, string> formFields);

    Task<ZiraatFinalizationResult> FinalizeAsync(
        string orderId,
        decimal amount,
        IReadOnlyDictionary<string, string> formFields,
        CancellationToken cancellationToken
    );
}

internal sealed class ZiraatPosGateway(
    HttpClient httpClient,
    IOptions<ZiraatPosOptions> options,
    ILogger<ZiraatPosGateway> logger
) : IZiraatPosGateway
{
    private const int MaxGatewayResponseCharacters = 1_000_000;
    private static readonly Action<ILogger, string, string, int, Exception?> LogThreeDStartFailed =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Warning,
            new EventId(3001, nameof(LogThreeDStartFailed)),
            "Ziraat 3D Secure start failed. Application={Application} OrderId={OrderId} StatusCode={StatusCode}"
        );
    private static readonly Action<ILogger, string, string, Exception?> LogThreeDStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(3002, nameof(LogThreeDStarted)),
            "Ziraat 3D Secure started. Application={Application} PaymentProvider=Ziraat OrderId={OrderId}"
        );
    private static readonly Action<ILogger, string, string, int, Exception?> LogAuthorizationFailed =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Warning,
            new EventId(3003, nameof(LogAuthorizationFailed)),
            "Ziraat authorization request failed. Application={Application} OrderId={OrderId} StatusCode={StatusCode}"
        );
    private readonly ZiraatPosOptions _options = options.Value;

    public async Task<string> StartThreeDSecureAsync(
        ZiraatPaymentRequest payment,
        CancellationToken cancellationToken
    )
    {
        EnsureConfigured();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientid"] = _options.MerchantId,
            ["storetype"] = "3d",
            ["hashAlgorithm"] = "ver3",
            ["islemtipi"] = "Auth",
            ["amount"] = payment.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["currency"] = "949",
            ["oid"] = payment.OrderId,
            ["okUrl"] = _options.CallbackUrl,
            ["failUrl"] = _options.CallbackUrl,
            ["lang"] = string.Equals(payment.Language, "en", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : "tr",
            ["pan"] = DigitsOnly(payment.Card.Number),
            ["cv2"] = DigitsOnly(payment.Card.SecurityCode),
            ["Ecom_Payment_Card_ExpDate_Year"] = payment.Card.ExpiryYear.ToString(CultureInfo.InvariantCulture),
            ["Ecom_Payment_Card_ExpDate_Month"] = payment.Card.ExpiryMonth.ToString("00", CultureInfo.InvariantCulture),
            ["rnd"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        };
        fields["hash"] = ZiraatPosHash.Create(fields, _options.StoreKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.GatewayUrl)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (!response.IsSuccessStatusCode)
        {
            LogThreeDStartFailed(
                logger,
                _options.ApplicationName,
                payment.OrderId,
                (int)response.StatusCode,
                null
            );
            throw new PaymentGatewayException("Banka doğrulama ekranı başlatılamadı.");
        }

        if (response.Content.Headers.ContentLength > MaxGatewayResponseCharacters)
        {
            throw new PaymentGatewayException("Banka yanıtı beklenen boyutu aşıyor.");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html) || html.Length > MaxGatewayResponseCharacters)
        {
            throw new PaymentGatewayException("Banka doğrulama ekranı alınamadı.");
        }

        LogThreeDStarted(
            logger,
            _options.ApplicationName,
            payment.OrderId,
            null
        );
        return html;
    }

    public bool VerifyCallback(IReadOnlyDictionary<string, string> formFields)
    {
        EnsureConfigured();
        return ZiraatPosHash.Verify(formFields, _options.StoreKey);
    }

    public async Task<ZiraatFinalizationResult> FinalizeAsync(
        string orderId,
        decimal amount,
        IReadOnlyDictionary<string, string> formFields,
        CancellationToken cancellationToken
    )
    {
        EnsureConfigured();

        var xml = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "CC5Request",
                new XElement("Name", _options.ApiUser),
                new XElement("Password", _options.ApiPassword),
                new XElement("ClientId", _options.ClientId),
                new XElement("Type", "Auth"),
                new XElement("OrderId", orderId),
                new XElement("Total", amount.ToString("0.00", CultureInfo.InvariantCulture)),
                new XElement("Currency", "949"),
                new XElement("Mode", "P"),
                new XElement("Number", Required(formFields, "md")),
                new XElement("Expires", string.Empty),
                new XElement("Cvv2Val", string.Empty),
                new XElement("PayerTxnId", Required(formFields, "xid")),
                new XElement("PayerSecurityLevel", Required(formFields, "eci")),
                new XElement("PayerAuthenticationCode", Required(formFields, "cavv"))
            )
        );

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ApiUrl)
        {
            Content = new StringContent(
                xml.ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8,
                "application/xml"
            ),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (!response.IsSuccessStatusCode)
        {
            LogAuthorizationFailed(
                logger,
                _options.ApplicationName,
                orderId,
                (int)response.StatusCode,
                null
            );
            throw new PaymentGatewayException("Banka ödeme onayı alınamadı.");
        }

        var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);
        XDocument document;
        try
        {
            document = XDocument.Parse(responseXml, LoadOptions.None);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException)
        {
            throw new PaymentGatewayException("Banka geçersiz bir ödeme yanıtı döndürdü.", exception);
        }

        var bankResponse = Value(document, "Response");
        var returnCode = Value(document, "ProcReturnCode");
        var approved = string.Equals(bankResponse, "Approved", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(returnCode) || returnCode == "00");
        return new ZiraatFinalizationResult(
            approved,
            Value(document, "AuthCode"),
            Value(document, "HostLogKey"),
            returnCode,
            Value(document, "ErrMsg")
        );
    }

    private void EnsureConfigured()
    {
        if (!_options.Enabled)
        {
            throw new PaymentConfigurationException("Ödeme sistemi henüz etkin değil.");
        }

        var required = new[]
        {
            _options.GatewayUrl,
            _options.ApiUrl,
            _options.MerchantId,
            _options.ClientId,
            _options.StoreKey,
            _options.ApiUser,
            _options.ApiPassword,
            _options.CallbackUrl,
            _options.FrontendOrigin,
        };
        if (required.Any(string.IsNullOrWhiteSpace))
        {
            throw new PaymentConfigurationException("Ziraat POS yapılandırması eksik.");
        }

        if (!Uri.TryCreate(_options.GatewayUrl, UriKind.Absolute, out var gatewayUri)
            || gatewayUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(_options.ApiUrl, UriKind.Absolute, out var apiUri)
            || apiUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(_options.CallbackUrl, UriKind.Absolute, out var callbackUri)
            || callbackUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(_options.FrontendOrigin, UriKind.Absolute, out _))
        {
            throw new PaymentConfigurationException("Ziraat POS adresleri geçersiz.");
        }
    }

    private static string Required(
        IReadOnlyDictionary<string, string> fields,
        string name
    ) => ZiraatPosHash.TryGet(fields, name, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new PaymentValidationException("3D Secure yanıtı eksik.");

    private static string DigitsOnly(string value) => string.Concat(
        value.Where(char.IsAsciiDigit)
    );

    private static string? Value(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Name.LocalName,
            name,
            StringComparison.OrdinalIgnoreCase
        ))
        ?.Value
        .Trim();
}
