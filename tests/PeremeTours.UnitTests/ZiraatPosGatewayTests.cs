using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.UnitTests;

public sealed class ZiraatPosGatewayTests
{
    private const string OrderId = "PRM-TEST-GATEWAY";
    private const string HostReference = "TEST-HOST-REFERENCE";
    private static readonly Dictionary<string, string> CallbackFields = new()
    {
        ["md"] = "test-md", ["xid"] = "test-xid", ["eci"] = "05", ["cavv"] = "test-cavv",
    };

    [Theory]
    [InlineData("HostRefNum")]
    [InlineData("HostLogKey")]
    public async Task ApprovedResponseUsesRealBankReferenceAndOnlySendsAuthOnce(string referenceField)
    {
        using var fixture = new Fixture(ApprovedResponse(referenceField).ToString());
        var result = await fixture.FinalizeAsync();

        Assert.True(result.IsApproved);
        Assert.Equal("TEST01", result.AuthCode);
        Assert.Equal(HostReference, result.HostReference);
        Assert.Equal("00", result.ErrorCode);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.Empty(fixture.Logger.Messages);
        Assert.Equal("Auth", fixture.Handler.RequestXml!.Root!.Element("Type")!.Value);
        Assert.Equal(OrderId, fixture.Handler.RequestXml.Root.Element("OrderId")!.Value);
        Assert.Equal("350.00", fixture.Handler.RequestXml.Root.Element("Total")!.Value);
    }

    [Fact]
    public async Task StandardHostRefNumTakesPrecedenceOverLegacyAlias()
    {
        var document = ApprovedResponse();
        document.Root!.Add(new XElement("HostLogKey", "legacy-reference"));
        using var fixture = new Fixture(document.ToString());
        Assert.Equal(HostReference, (await fixture.FinalizeAsync()).HostReference);
    }

    [Fact]
    public async Task NamespacedResponseTrimsValues()
    {
        var document = ApprovedResponse();
        foreach (var element in document.Descendants())
        {
            element.Name = XName.Get(element.Name.LocalName, "urn:test-cc5");
            if (!element.HasElements) element.Value = " " + element.Value + " ";
        }
        using var fixture = new Fixture(document.ToString());
        Assert.True((await fixture.FinalizeAsync()).IsApproved);
    }

    [Theory]
    [InlineData("OrderId", null, "ORDER_MISMATCH")]
    [InlineData("OrderId", "different-order", "ORDER_MISMATCH")]
    [InlineData("ProcReturnCode", null, "APPROVAL_CODE_MISMATCH")]
    [InlineData("ProcReturnCode", "05", "APPROVAL_CODE_MISMATCH")]
    [InlineData("AuthCode", null, "AUTH_CODE_MISSING")]
    [InlineData("AuthCode", " ", "AUTH_CODE_MISSING")]
    [InlineData("HostRefNum", null, "HOST_REFERENCE_MISSING")]
    [InlineData("HostRefNum", " ", "HOST_REFERENCE_MISSING")]
    [InlineData("Response", "unknown", "RESPONSE_STATUS_INVALID")]
    [InlineData("Response", "Error", "RESPONSE_STATUS_INVALID")]
    public async Task IncompleteOrInconsistentApprovalStaysUncertain(string field, string? value, string reason)
    {
        var document = ApprovedResponse();
        if (value is null) document.Root!.Element(field)!.Remove();
        else document.Root!.Element(field)!.Value = value;
        using var fixture = new Fixture(document.ToString());

        await Assert.ThrowsAsync<PaymentGatewayException>(fixture.FinalizeAsync);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.Contains(reason, Assert.Single(fixture.Logger.Messages), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Declined", "05")]
    [InlineData("Error", "99")]
    public async Task DeclinedAndErrorResponsesDoNotBecomeApprovals(string response, string code)
    {
        var document = new XDocument(new XElement("CC5Response",
            new XElement("OrderId", OrderId), new XElement("Response", response),
            new XElement("ProcReturnCode", code), new XElement("AuthCode", ""),
            new XElement("HostRefNum", ""), new XElement("ErrMsg", "User is not authenticated to perform this process."),
            new XElement("Extra", new XElement("ERRORCODE", "CORE-2201"))));
        using var fixture = new Fixture(document.ToString());
        var result = await fixture.FinalizeAsync();
        Assert.False(result.IsApproved);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal("CORE-2201", result.ErrorDetailCode);
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    [Theory]
    [InlineData("", "INVALID_RESPONSE_SIZE")]
    [InlineData("<CC5Response>do-not-log-sensitive-marker", "MALFORMED_XML")]
    [InlineData("<Unexpected>do-not-log-sensitive-marker</Unexpected>", "UNEXPECTED_RESPONSE_ROOT")]
    public async Task InvalidXmlLogsOnlyFixedDiagnosticCode(string xml, string reason)
    {
        using var fixture = new Fixture(xml);
        var error = await Assert.ThrowsAsync<PaymentGatewayException>(fixture.FinalizeAsync);
        var message = Assert.Single(fixture.Logger.Messages);
        Assert.Contains(reason, message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-sensitive-marker", message + error, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task NestedFieldsCannotSubstituteForAuthorizationResult()
    {
        var document = new XDocument(new XElement("CC5Response",
            new XElement("Extra", ApprovedResponse().Root!.Elements())));
        using var fixture = new Fixture(document.ToString());
        await Assert.ThrowsAsync<PaymentGatewayException>(fixture.FinalizeAsync);
        Assert.Contains("RESPONSE_STATUS_INVALID", Assert.Single(fixture.Logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureIsNotRetried()
    {
        using var fixture = new Fixture("do-not-log-sensitive-marker", HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<PaymentGatewayException>(fixture.FinalizeAsync);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.DoesNotContain("do-not-log-sensitive-marker", Assert.Single(fixture.Logger.Messages), StringComparison.Ordinal);
    }

    private static XDocument ApprovedResponse(string referenceField = "HostRefNum") => new(new XElement("CC5Response",
        new XElement("OrderId", OrderId), new XElement("Response", "Approved"),
        new XElement("AuthCode", "TEST01"), new XElement(referenceField, HostReference),
        new XElement("ProcReturnCode", "00"), new XElement("TransId", "test-transaction")));

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        private readonly ZiraatPosGateway _gateway;
        public Fixture(string xml, HttpStatusCode status = HttpStatusCode.OK)
        {
            Handler = new ResponseHandler(xml, status);
            _client = new HttpClient(Handler);
            _gateway = new ZiraatPosGateway(_client, Options.Create(new ZiraatPosOptions
            {
                Enabled = true, MerchantId = "fake-merchant", ClientId = "fake-client", StoreKey = "fake-store-key",
                ApiUser = "fake-user", ApiPassword = "fake-password", GatewayUrl = "https://bank.example.test/start",
                ApiUrl = "https://bank.example.test/api", CallbackUrl = "https://api.example.test/callback",
                FrontendOrigin = "https://example.test",
            }), Logger);
        }
        public ResponseHandler Handler { get; }
        public CaptureLogger Logger { get; } = new();
        public Task<ZiraatFinalizationResult> FinalizeAsync() =>
            _gateway.FinalizeAsync(OrderId, 350m, CallbackFields, CancellationToken.None);
        public void Dispose() => _client.Dispose();
    }

    private sealed class ResponseHandler(string responseXml, HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public XDocument? RequestXml { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("bank.example.test", request.RequestUri!.Host);
            RequestXml = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(responseXml, Encoding.UTF8, "application/xml") };
        }
    }

    private sealed class CaptureLogger : ILogger<ZiraatPosGateway>
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
