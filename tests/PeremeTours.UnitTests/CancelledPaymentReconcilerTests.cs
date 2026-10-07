using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.UnitTests;

public sealed class CancelledPaymentReconcilerTests
{
    private const string OrderId = "PRM-MOCK-CANCELLATION";
    private const string BankTransaction = "MOCK-BANK-TRANSACTION";
    private static readonly string[] QueryFields = ["Name", "Password", "ClientId", "OrderId", "Extra"];

    [Fact]
    public async Task VerifiedCancellationClosesOnlyTheMatchingUnissuedOrderAndIsIdempotent()
    {
        using var fixture = new Fixture();
        var unrelated = NewTicket("PRM-OTHER-ORDER");
        fixture.Db.TourTickets.Add(unrelated);
        fixture.Db.TicketErrorRecords.Add(new TicketErrorRecord
        {
            Id = Guid.NewGuid(), TicketId = fixture.Ticket.Id, Stage = TicketErrorStage.Payment,
            Code = "BANK_RESULT_UNKNOWN", Message = "Original error", CreatedAtUtc = DateTime.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.ReconcileAsync();
        await fixture.ReconcileAsync();
        fixture.Db.ChangeTracker.Clear();
        var ticket = await fixture.Db.TourTickets.SingleAsync(item => item.TicketCode == OrderId);
        Assert.Equal(TicketStatus.Cancelled, ticket.Status);
        Assert.Equal(TicketPaymentStatus.Refunded, ticket.PaymentStatus);
        Assert.Equal(TicketingStatus.NotRequired, ticket.TicketingStatus);
        Assert.Null(ticket.PaymentFailureCode);
        Assert.Null(ticket.PaymentFailureMessage);
        Assert.NotNull(ticket.PaymentAttemptId);
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.Equal(TicketPaymentStatus.ReviewRequired, (await fixture.Db.TourTickets.SingleAsync(item => item.Id == unrelated.Id)).PaymentStatus);
        Assert.Single(await fixture.Db.TicketErrorRecords.ToListAsync());
        Assert.Empty(await fixture.Db.PaymentEmails.ToListAsync());
        Assert.Equal(QueryFields,
            fixture.Handler.LastRequest!.Root!.Elements().Select(item => item.Name.LocalName).ToArray());
        Assert.Equal("QUERY", fixture.Handler.LastRequest.Root.Element("Extra")!.Element("ORDERSTATUS")!.Value);
    }

    [Theory]
    [InlineData("Response", "Declined")]
    [InlineData("ProcReturnCode", "99")]
    [InlineData("OrderId", "PRM-OTHER")]
    [InlineData("TransId", "OTHER-TRANSACTION")]
    [InlineData("ORD_ID", "PRM-OTHER")]
    [InlineData("CHARGE_TYPE_CD", "C")]
    [InlineData("TRANS_STAT", "C")]
    [InlineData("TRANS_STAT", "PN")]
    [InlineData("PROC_RET_CD", "99")]
    [InlineData("ORIG_TRANS_AMT", "350")]
    [InlineData("ORIG_TRANS_AMT", "35000.00")]
    [InlineData("CAPTURE_AMT", "1")]
    public async Task MismatchedBankResultCannotChangeTheSiteRecord(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Handler.Xml.Descendants(field).Single().Value = value;
        await Assert.ThrowsAsync<PaymentGatewayException>(() => fixture.ReconcileAsync());
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(TicketPaymentStatus.ReviewRequired, (await fixture.Db.TourTickets.SingleAsync()).PaymentStatus);
        Assert.Equal(TicketStatus.Pending, (await fixture.Db.TourTickets.SingleAsync()).Status);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task DuplicateApprovalFieldsCannotVerifyCancellation()
    {
        using var fixture = new Fixture();
        fixture.Handler.Xml.Root!.Add(new XElement("OrderId", OrderId));
        await Assert.ThrowsAsync<PaymentGatewayException>(() => fixture.ReconcileAsync());
        Assert.Equal(TicketPaymentStatus.ReviewRequired, fixture.Ticket.PaymentStatus);
    }

    [Theory]
    [InlineData("issued")]
    [InlineData("voucher")]
    [InlineData("amount")]
    [InlineData("processing")]
    [InlineData("provider")]
    public async Task UnsafeSiteStatePreventsEvenTheBankQuery(string state)
    {
        using var fixture = new Fixture();
        switch (state)
        {
            case "issued": fixture.Ticket.TicketingStatus = TicketingStatus.Issued; break;
            case "voucher": fixture.Ticket.ExternalVoucherGuid = "mock-voucher"; break;
            case "amount": fixture.Ticket.Amount = 351; break;
            case "processing": fixture.Ticket.PaymentStatus = TicketPaymentStatus.Processing; break;
            case "provider": fixture.Ticket.PaymentProvider = "OtherBank"; break;
        }
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<PaymentValidationException>(() => fixture.ReconcileAsync());
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task UnavailableBankDoesNotRetryOrCloseTheRecord()
    {
        using var fixture = new Fixture();
        fixture.Handler.StatusCode = HttpStatusCode.BadGateway;
        await Assert.ThrowsAsync<PaymentGatewayException>(() => fixture.ReconcileAsync());
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.Equal(TicketPaymentStatus.ReviewRequired, fixture.Ticket.PaymentStatus);
    }

    [Fact]
    public async Task MalformedBankXmlIsNeverExposedByTheException()
    {
        using var fixture = new Fixture();
        fixture.Handler.RawXml = "<CC5Response>fake-password 4111111111111111";
        var exception = await Assert.ThrowsAsync<PaymentGatewayException>(() => fixture.ReconcileAsync());
        Assert.DoesNotContain("411111", exception.ToString());
        Assert.DoesNotContain("fake-password", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Equal(TicketPaymentStatus.ReviewRequired, fixture.Ticket.PaymentStatus);
    }

    private static TourTicket NewTicket(string code) => new()
    {
        Id = Guid.NewGuid(), TicketCode = code, TourName = "Mock tour", TourDate = new DateOnly(2099, 1, 1),
        CustomerName = "Test", CustomerEmail = "test@example.com", Amount = 350m, Currency = "TRY", GuestCount = 1,
        Channel = TicketChannel.Web, Status = TicketStatus.Pending, PaymentProvider = "Ziraat",
        PaymentStatus = TicketPaymentStatus.ReviewRequired, TicketingStatus = TicketingStatus.Pending,
        PaymentAttemptId = Guid.NewGuid(), PaymentFailureCode = "BANK_RESULT_UNKNOWN",
        PaymentFailureMessage = "Original error", CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly HttpClient _httpClient;
        private readonly CancelledPaymentReconciler _reconciler;
        public Fixture()
        {
            _connection.Open();
            Db = new PeremeToursDbContext(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection).Options);
            Db.Database.EnsureCreated();
            Ticket = NewTicket(OrderId);
            Db.TourTickets.Add(Ticket);
            Db.SaveChanges();
            Handler = new ResponseHandler();
            _httpClient = new HttpClient(Handler);
            _reconciler = new CancelledPaymentReconciler(Db, _httpClient, Options.Create(new ZiraatPosOptions
            {
                Enabled = true, MerchantId = "fake-merchant", ClientId = "fake-client", StoreKey = "fake-key",
                ApiUser = "fake-user", ApiPassword = "fake-password", ApiUrl = "https://bank.example.test/api",
                GatewayUrl = "https://bank.example.test/start", CallbackUrl = "https://api.example.test/callback",
                FrontendOrigin = "https://example.test",
            }), TimeProvider.System, NullLogger<CancelledPaymentReconciler>.Instance);
        }
        public PeremeToursDbContext Db { get; }
        public TourTicket Ticket { get; }
        public ResponseHandler Handler { get; }
        public Task ReconcileAsync() => _reconciler.ReconcileAsync(OrderId, 350m, BankTransaction, CancellationToken.None);
        public void Dispose() { _httpClient.Dispose(); Db.Dispose(); _connection.Dispose(); }
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public XDocument Xml { get; } = XDocument.Parse($"""
            <CC5Response><OrderId>{OrderId}</OrderId><TransId>{BankTransaction}</TransId>
            <Response>Approved</Response><ProcReturnCode>00</ProcReturnCode><Extra>
            <ORD_ID>{OrderId}</ORD_ID><CHARGE_TYPE_CD>S</CHARGE_TYPE_CD><TRANS_STAT>V</TRANS_STAT>
            <ORIG_TRANS_AMT>35000</ORIG_TRANS_AMT><CAPTURE_AMT>35000</CAPTURE_AMT><PROC_RET_CD>00</PROC_RET_CD>
            </Extra></CC5Response>
            """);
        public int Calls { get; private set; }
        public XDocument? LastRequest { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string? RawXml { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("bank.example.test", request.RequestUri!.Host);
            LastRequest = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Empty(LastRequest.Descendants("Type"));
            Assert.Empty(LastRequest.Descendants("Number"));
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(RawXml ?? Xml.ToString(), Encoding.UTF8, "application/xml"),
            };
        }
    }
}
