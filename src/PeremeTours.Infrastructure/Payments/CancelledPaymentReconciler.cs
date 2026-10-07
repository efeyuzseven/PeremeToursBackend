using System.Data;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Payments;

// Operations-only reconciliation of a cancellation ALREADY approved by the bank.
// This service can only query ORDERSTATUS; it cannot charge, void, refund or issue tickets.
public sealed class CancelledPaymentReconciler(
    PeremeToursDbContext db,
    HttpClient httpClient,
    IOptions<ZiraatPosOptions> options,
    TimeProvider timeProvider,
    ILogger<CancelledPaymentReconciler> logger)
{
    private static readonly Action<ILogger, string, string, Exception?> LogReconciled =
        LoggerMessage.Define<string, string>(LogLevel.Information,
            new EventId(3201, nameof(LogReconciled)),
            "Bank cancellation reconciled. Application={Application} OrderId={OrderId} PaymentStatus=Refunded TicketStatus=Cancelled");

    public async Task ReconcileAsync(string orderId, decimal expectedAmount,
        string expectedBankTransaction, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBankTransaction);
        if (expectedAmount <= 0 || orderId.Length > 64 || expectedBankTransaction.Length > 128)
            throw new PaymentValidationException("İptal kontrol bilgileri geçersiz.");

        // Hold a serializable transaction through the verification and update. A concurrent
        // payment/ticket change must abort reconciliation, not be silently overwritten.
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var ticket = await db.TourTickets.Include(item => item.Passengers)
            .SingleOrDefaultAsync(item => item.TicketCode == orderId, cancellationToken)
            ?? throw new PaymentValidationException("İptal edilecek ödeme kaydı bulunamadı.");
        if (ticket.Amount != expectedAmount || ticket.Currency != "TRY" || ticket.PaymentProvider != "Ziraat")
            throw new PaymentValidationException("İptal kontrolü tutar, para birimi veya banka kaydıyla eşleşmedi.");
        if (ticket.Status == TicketStatus.Cancelled && ticket.PaymentStatus == TicketPaymentStatus.Refunded)
            return;
        if (ticket.Status != TicketStatus.Pending
            || ticket.PaymentStatus is not (TicketPaymentStatus.ReviewRequired or TicketPaymentStatus.Paid)
            || ticket.TicketingStatus is not (TicketingStatus.Pending or TicketingStatus.NotRequired)
            || ticket.ExternalVoucherGuid is not null
            || ticket.Passengers.Any(item => item.Pnr is not null || item.ExternalTicketGuid is not null)
            || await db.PaymentEmails.AnyAsync(item => item.TicketId == ticket.Id, cancellationToken))
            throw new PaymentValidationException("Bu işlem otomatik kapatılamaz; bilet veya mail kaydı ayrıca kontrol edilmelidir.");

        var settings = options.Value;
        if (!settings.IsConfigured)
            throw new PaymentConfigurationException("Banka sorgu yapılandırması eksik.");
        var requestXml = new XDocument(new XElement("CC5Request",
            new XElement("Name", settings.ApiUser),
            new XElement("Password", settings.ApiPassword),
            new XElement("ClientId", settings.ClientId),
            new XElement("OrderId", orderId),
            new XElement("Extra", new XElement("ORDERSTATUS", "QUERY"))));
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl)
        {
            Content = new StringContent(requestXml.ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8, "application/xml"),
        };
        using var response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PaymentGatewayException("Banka iptal sorgusu doğrulanamadı.");
        var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseXml) || responseXml.Length > 256_000)
            throw new PaymentGatewayException("Banka iptal yanıtı doğrulanamadı.");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(responseXml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 256_000,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // Never expose raw bank XML, PAN or credentials in exceptions/logs.
            throw new PaymentGatewayException("Banka iptal yanıtı doğrulanamadı.");
        }
        if (document.Root?.Name.LocalName != "CC5Response")
            throw new PaymentGatewayException("Banka iptal yanıtı doğrulanamadı.");
        var extras = document.Root.Elements().Where(item => item.Name.LocalName == "Extra").ToArray();
        var extra = extras.Length == 1 ? extras[0] : null;
        var valid = Child(document.Root, "Response") == "Approved"
            && Child(document.Root, "ProcReturnCode") == "00"
            && Child(document.Root, "OrderId") == orderId
            && Child(document.Root, "TransId") == expectedBankTransaction
            && Child(extra, "ORD_ID") == orderId
            && Child(extra, "CHARGE_TYPE_CD") == "S"
            && Child(extra, "TRANS_STAT") is "V" or "CNCL"
            && Child(extra, "PROC_RET_CD") == "00"
            && decimal.TryParse(Child(extra, "ORIG_TRANS_AMT"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var originalMinorUnits)
            && originalMinorUnits == expectedAmount * 100
            && decimal.TryParse(Child(extra, "CAPTURE_AMT"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var capturedMinorUnits)
            && capturedMinorUnits == expectedAmount * 100;
        if (!valid)
            throw new PaymentGatewayException("Banka aynı sipariş, işlem ve tutar için iptali doğrulamadı; site kaydı değiştirilmedi.");

        ticket.Status = TicketStatus.Cancelled;
        // Refunded also represents a bank-confirmed reversal/void in the existing payment enum.
        ticket.PaymentStatus = TicketPaymentStatus.Refunded;
        ticket.TicketingStatus = TicketingStatus.NotRequired;
        ticket.PaymentFailureCode = null;
        ticket.PaymentFailureMessage = null;
        ticket.UpdatedAtUtc = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // Keep original error history and the recovery token for clients to read the final result.
        LogReconciled(logger, settings.ApplicationName, orderId, null);
    }

    private static string? Child(XElement? parent, string name)
    {
        var matches = parent?.Elements().Where(item => item.Name.LocalName == name).ToArray();
        // Duplicate fields are ambiguous, not legitimate evidence of cancellation.
        return matches is { Length: 1 } ? matches[0].Value.Trim() : null;
    }
}
