using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.Infrastructure.Tickets;

internal sealed class ZiraatCancellationGateway(HttpClient http, IOptions<ZiraatPosOptions> options) : IBankCancellationGateway
{
    public async Task<BankCancellationCheck> CheckAsync(TourTicket ticket, CancellationToken cancellationToken)
    {
        const string failure = "BANK_CANCELLATION_CHECK_FAILED";
        var root = await RequestAsync(ticket, new XElement("Extra", new XElement("ORDERSTATUS", "QUERY")), failure, cancellationToken);
        var extra = Element(root, "Extra");
        var transaction = Child(root, "TransId");
        var state = Child(extra, "TRANS_STAT");
        if (!Approved(root, ticket.TicketCode) || !Identifier(transaction) || Child(extra, "ORD_ID") != ticket.TicketCode
            || Child(extra, "CHARGE_TYPE_CD") != "S" || Child(extra, "PROC_RET_CD") != "00"
            || !MinorAmountMatches(Child(extra, "ORIG_TRANS_AMT"), ticket.Amount)
            || !MinorAmountMatches(Child(extra, "CAPTURE_AMT"), ticket.Amount)
            || state is not ("A" or "C" or "S" or "V" or "CNCL"))
            throw new CancellationGatewayException(failure);
        return new(transaction!, state == "S" ? "Credit" : "Void", state is "V" or "CNCL");
    }

    public async Task<string> ReverseAsync(TourTicket ticket, BankCancellationCheck check, CancellationToken cancellationToken)
    {
        if (check.AlreadyReversed || check.Operation is not ("Void" or "Credit") || !Identifier(check.TransactionId))
            throw new CancellationGatewayException("BANK_REVERSAL_UNKNOWN");
        // No PAN/CVC or new charge. Refund the entire server-side amount against the original transaction.
        var body = new XElement("Operation",
            new XElement("Type", check.Operation), new XElement("TransId", check.TransactionId));
        if (check.Operation == "Credit")
        {
            body.Add(new XElement("Total", ticket.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
            body.Add(new XElement("Currency", "949"));
        }
        var root = await RequestAsync(ticket, body, "BANK_REVERSAL_UNKNOWN", cancellationToken);
        if (!Approved(root, ticket.TicketCode)) throw new CancellationGatewayException("BANK_REVERSAL_REJECTED");
        var reference = Child(root, "TransId");
        if (!Identifier(reference)) throw new CancellationGatewayException("BANK_REVERSAL_UNKNOWN");
        return reference!;
    }

    private async Task<XElement> RequestAsync(TourTicket ticket, XElement operation, string failure, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured || ticket.PaymentProvider != "Ziraat" || ticket.Currency != "TRY"
            || ticket.Amount <= 0 || ticket.TicketCode.Length > 64)
            throw new CancellationGatewayException(failure);
        var xml = new XElement("CC5Request", new XElement("Name", settings.ApiUser),
            new XElement("Password", settings.ApiPassword), new XElement("ClientId", settings.ClientId),
            new XElement("OrderId", ticket.TicketCode));
        if (operation.Name.LocalName == "Operation") xml.Add(operation.Elements());
        else xml.Add(operation);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl)
        {
            Content = new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml"),
        };
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new CancellationGatewayException(failure);
            var text = await ZiraatResponseReader.ReadAsync(response.Content, cancellationToken);
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256_000 });
            var root = XDocument.Load(reader).Root;
            if (root?.Name.LocalName != "CC5Response") throw new CancellationGatewayException(failure);
            return root;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or XmlException
            or PeremeTours.Application.Payments.PaymentGatewayException or IOException or NotSupportedException)
        {
            // Never expose bank responses, credentials, PAN or raw exception messages.
            throw new CancellationGatewayException(failure);
        }
    }

    private static bool Approved(XElement root, string order) => Child(root, "Response") == "Approved"
        && Child(root, "ProcReturnCode") == "00" && Child(root, "OrderId") == order;
    private static bool MinorAmountMatches(string? value, decimal amount) =>
        decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) && minor == amount * 100;
    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static XElement? Element(XElement? parent, string name)
    {
        var matches = parent?.Elements().Where(item => item.Name.LocalName == name).ToArray();
        return matches is { Length: 1 } ? matches[0] : null;
    }
    private static string? Child(XElement? parent, string name) => Element(parent, name)?.Value.Trim();
}
