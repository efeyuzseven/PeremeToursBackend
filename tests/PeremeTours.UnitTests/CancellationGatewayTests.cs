using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Tickets;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.UnitTests;

public sealed class CancellationGatewayTests
{
    private const string Voucher = "11111111-1111-4111-8111-111111111111";
    private const string TicketGuid = "22222222-2222-4222-8222-222222222222";
    private static TourTicket Ticket() => new() { Id = Guid.NewGuid(), TicketCode = "PRM-MOCK-CANCEL", TourName = "Test", CustomerName = "Demo",
        CustomerEmail = "test@example.test", Currency = "TRY", PaymentProvider = "Ziraat", Amount = 350, GuestCount = 1, ExternalVoucherGuid = Voucher,
        Passengers = [new TourPassenger { FirstName = "Demo", LastName = "Guest", Gender = "male", Nationality = "TR", IdentityNumber = "12345678901", ExternalTicketGuid = TicketGuid, Pnr = "MOCKPNR" }] };
    private static ZiraatPosOptions BankOptions() => new() { Enabled = true, ApiUser = "mock-user", ApiPassword = "mock-password", StoreKey = "mock-key", ClientId = "mock-store", MerchantId = "mock-store",
        ApiUrl = "https://bank.example.test/api", GatewayUrl = "https://bank.example.test/3d", CallbackUrl = "https://web.example.test/callback", FrontendOrigin = "https://web.example.test" };
    private static XDocument BankResponse(string state = "C") => XDocument.Parse($"""
        <CC5Response><OrderId>PRM-MOCK-CANCEL</OrderId><TransId>MOCKTX</TransId><Response>Approved</Response><ProcReturnCode>00</ProcReturnCode>
        <Extra><ORD_ID>PRM-MOCK-CANCEL</ORD_ID><CHARGE_TYPE_CD>S</CHARGE_TYPE_CD><TRANS_STAT>{state}</TRANS_STAT>
        <ORIG_TRANS_AMT>35000</ORIG_TRANS_AMT><CAPTURE_AMT>35000</CAPTURE_AMT><PROC_RET_CD>00</PROC_RET_CD></Extra></CC5Response>
        """);

    [Theory]
    [InlineData("C", "Void", false)]
    [InlineData("A", "Void", false)]
    [InlineData("S", "Credit", false)]
    [InlineData("V", "Void", true)]
    public async Task QuerySelectsOperationFromBankStatusWithoutAnyFinancialType(string state, string expected, bool already)
    {
        using var handler = new Handler(async request => {
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Empty(xml.Descendants("Type")); Assert.Empty(xml.Descendants("Number")); Assert.Empty(xml.Descendants("Cvv2Val"));
            return Xml(BankResponse(state));
        });
        var check = await new ZiraatCancellationGateway(new HttpClient(handler), Options.Create(BankOptions())).CheckAsync(Ticket(), CancellationToken.None);
        Assert.Equal(expected, check.Operation); Assert.Equal(already, check.AlreadyReversed);
    }

    [Theory]
    [InlineData("OrderId", "OTHER")]
    [InlineData("ORD_ID", "OTHER")]
    [InlineData("CHARGE_TYPE_CD", "C")]
    [InlineData("ORIG_TRANS_AMT", "34900")]
    [InlineData("CAPTURE_AMT", "34900")]
    [InlineData("PROC_RET_CD", "99")]
    [InlineData("TRANS_STAT", "D")]
    public async Task QueryRejectsWrongOrderAmountOrState(string field, string value)
    {
        var document = BankResponse(); document.Descendants(field).Single().Value = value;
        using var handler = new Handler(_ => Task.FromResult(Xml(document)));
        var failure = await Assert.ThrowsAsync<CancellationGatewayException>(() => new ZiraatCancellationGateway(new HttpClient(handler), Options.Create(BankOptions())).CheckAsync(Ticket(), CancellationToken.None));
        Assert.Equal("BANK_CANCELLATION_CHECK_FAILED", failure.Code);
    }

    [Theory]
    [InlineData("Void")]
    [InlineData("Credit")]
    public async Task ReversalContainsOnlyOriginalTransactionAndFullServerAmount(string operation)
    {
        using var handler = new Handler(async request => {
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(operation, xml.Descendants("Type").Single().Value);
            Assert.Equal("MOCKTX", xml.Descendants("TransId").Single().Value);
            Assert.Equal("PRM-MOCK-CANCEL", xml.Descendants("OrderId").Single().Value);
            Assert.Empty(xml.Descendants("Number")); Assert.Empty(xml.Descendants("Cvv2Val"));
            if (operation == "Credit") { Assert.Equal("350.00", xml.Descendants("Total").Single().Value); Assert.Equal("949", xml.Descendants("Currency").Single().Value); }
            return Xml(BankResponse());
        });
        Assert.Equal("MOCKTX", await new ZiraatCancellationGateway(new HttpClient(handler), Options.Create(BankOptions())).ReverseAsync(Ticket(), new("MOCKTX", operation, false), CancellationToken.None));
    }

    [Theory]
    [InlineData("Response", "Declined")]
    [InlineData("ProcReturnCode", "99")]
    [InlineData("OrderId", "OTHER")]
    [InlineData("TransId", "")]
    public async Task ReversalCannotBeApprovedFromAnInvalidBankResponse(string field, string value)
    {
        var document = BankResponse(); document.Descendants(field).Single().Value = value;
        using var handler = new Handler(_ => Task.FromResult(Xml(document)));
        await Assert.ThrowsAsync<CancellationGatewayException>(() => new ZiraatCancellationGateway(new HttpClient(handler), Options.Create(BankOptions())).ReverseAsync(Ticket(), new("MOCKTX", "Void", false), CancellationToken.None));
    }

    [Fact]
    public async Task ProviderCancellationPostsAJsonStringOnlyAfterMatchingGenuineTickets()
    {
        var posts = 0;
        using var handler = new Handler(async request => {
            Assert.Equal("mock-key", request.Headers.GetValues("X-Api-Key").Single());
            if (request.Method == HttpMethod.Get) return Json(new { success = true, biletler = new[] { new { Guid = TicketGuid, Pnr = "MOCKPNR", ToplamTutar = 350m } } });
            posts++; Assert.Equal(Voucher, JsonSerializer.Deserialize<string>(await request.Content!.ReadAsStringAsync()));
            Assert.Equal("/api/data/web-bilet-iptal", request.RequestUri!.AbsolutePath);
            return Json(new { success = true });
        });
        await Provider(handler).CancelAsync(Ticket(), CancellationToken.None); Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("wrong-guid")]
    [InlineData("amount")]
    public async Task MismatchedVoucherNeverTriggersACancellation(string fault)
    {
        using var handler = new Handler(request => {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(new { success = true, biletler = new[] { new { Guid = fault == "wrong-guid" ? Voucher : TicketGuid, Pnr = "MOCKPNR", ToplamTutar = fault == "amount" ? 1m : 350m } } }));
        });
        var error = await Assert.ThrowsAsync<CancellationGatewayException>(() => Provider(handler).CancelAsync(Ticket(), CancellationToken.None));
        Assert.Equal("PROVIDER_CANCELLATION_CHECK_FAILED", error.Code);
    }

    private static EasyTicketCancellationGateway Provider(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://provider.example.test") },
        Options.Create(new EasyTicketOptions { BaseUrl = "https://provider.example.test", ApiKey = "mock-key" }));

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task TransientReadOnlyPrecheckRetriesThenCancelsExactlyOnce(int status)
    {
        var gets = 0; var posts = 0;
        using var handler = new Handler(request => {
            if (request.Method == HttpMethod.Get)
            {
                gets++;
                Assert.Contains("application/json", request.Headers.Accept.ToString());
                if (gets == 1) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
                return Task.FromResult(Json(new { success = true, biletler = new[] { new { Guid = TicketGuid, Pnr = "MOCKPNR", ToplamTutar = 350m } } }));
            }
            posts++; return Task.FromResult(Json(new { success = true }));
        });
        await Provider(handler).CancelAsync(Ticket(), CancellationToken.None);
        Assert.Equal(2, gets); Assert.Equal(1, posts);
    }

    [Fact]
    public async Task UnavailablePrecheckStopsAfterThreeReadsWithoutSendingCancellation()
    {
        var calls = 0;
        using var handler = new Handler(request => {
            Assert.Equal(HttpMethod.Get, request.Method); calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var error = await Assert.ThrowsAsync<CancellationGatewayException>(() => Provider(handler).CancelAsync(Ticket(), CancellationToken.None));
        Assert.Equal("PROVIDER_CANCELLATION_UNAVAILABLE", error.Code); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NetworkFailureRetriesOnlyTheReadOnlyPrecheck()
    {
        var gets = 0; var posts = 0;
        using var handler = new Handler(request => {
            if (request.Method == HttpMethod.Get)
            {
                if (++gets == 1) throw new HttpRequestException("Mock network error");
                return Task.FromResult(Json(new { success = true, biletler = new[] { new { Guid = TicketGuid, Pnr = "MOCKPNR", ToplamTutar = 350m } } }));
            }
            posts++; return Task.FromResult(Json(new { success = true }));
        });
        await Provider(handler).CancelAsync(Ticket(), CancellationToken.None);
        Assert.Equal(2, gets); Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task PermanentPrecheckFailuresDoNotAutomaticallyRetry(int status)
    {
        var calls = 0;
        using var handler = new Handler(request => {
            Assert.Equal(HttpMethod.Get, request.Method); calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        var error = await Assert.ThrowsAsync<CancellationGatewayException>(() => Provider(handler).CancelAsync(Ticket(), CancellationToken.None));
        Assert.Equal("PROVIDER_CANCELLATION_CHECK_FAILED", error.Code); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnavailableCancellationPostIsNeverRetried()
    {
        var gets = 0; var posts = 0;
        using var handler = new Handler(request => {
            if (request.Method == HttpMethod.Get)
            {
                gets++; return Task.FromResult(Json(new { success = true, biletler = new[] { new { Guid = TicketGuid, Pnr = "MOCKPNR", ToplamTutar = 350m } } }));
            }
            posts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var error = await Assert.ThrowsAsync<CancellationGatewayException>(() => Provider(handler).CancelAsync(Ticket(), CancellationToken.None));
        Assert.Equal("PROVIDER_CANCELLATION_UNKNOWN", error.Code); Assert.Equal(1, gets); Assert.Equal(1, posts);
    }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Xml(XDocument value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToString(), Encoding.UTF8, "text/xml") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
}
