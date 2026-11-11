using System.Globalization;
using ClosedXML.Excel;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Tickets;

// Builds the admin sales spreadsheet. Times are Istanbul local time, matching the admin screens.
internal static class TicketExcelExporter
{
    private static readonly string[] TurkishHeaders =
    [
        "Bilet No", "Satış Tarihi", "Tur", "Kalkış İskelesi", "Tur Tarihi", "Saat", "Durum", "Ödeme", "Kanal",
        "Müşteri", "E-posta", "Telefon", "Kişi Sayısı", "Yolcular", "Yolcu Tipleri", "Tutar", "Para Birimi",
        "Fatura İsteği", "Fatura Tipi", "Ad Soyad / Ünvan", "T.C. / Vergi No", "Vergi Dairesi", "Fatura E-postası", "Fatura Adresi",
        "İadeyi Yapan", "İadeyi Yapan E-posta", "İade Nedeni", "İade Zamanı",
    ];

    private static readonly string[] EnglishHeaders =
    [
        "Ticket No", "Sale Date", "Tour", "Departure Pier", "Tour Date", "Time", "Status", "Payment", "Channel",
        "Customer", "Email", "Phone", "Guests", "Passengers", "Passenger Types", "Amount", "Currency",
        "Invoice Request", "Invoice Type", "Name / Company", "Identity / Tax No", "Tax Office", "Invoice Email", "Invoice Address",
        "Refunded By", "Refunded By Email", "Refund Reason", "Refund Time",
    ];

    public static byte[] Create(IEnumerable<TourTicket> tickets, IReadOnlyDictionary<Guid, TicketCancellationActor> actors,
        bool turkish, TimeZoneInfo zone)
    {
        var headers = turkish ? TurkishHeaders : EnglishHeaders;
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(turkish ? "Tur Biletleri" : "Tour Tickets");
        for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];

        var row = 2;
        foreach (var ticket in tickets)
        {
            var passengers = ticket.Passengers.OrderBy(passenger => passenger.Sequence).ToList();
            var cancellation = ticket.Cancellation;
            var actor = cancellation is null ? null : actors.GetValueOrDefault(cancellation.ActorUserId);
            var cells = sheet.Row(row);
            cells.Cell(1).Value = ticket.TicketCode;
            cells.Cell(2).Value = TimeZoneInfo.ConvertTimeFromUtc(ticket.CreatedAtUtc.UtcDateTime, zone);
            cells.Cell(3).Value = ticket.TourName;
            cells.Cell(4).Value = ticket.DeparturePortName;
            cells.Cell(5).Value = ticket.TourDate.ToDateTime(TimeOnly.MinValue);
            cells.Cell(6).Value = ticket.DepartureTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            cells.Cell(7).Value = StatusLabel(ticket.Status, turkish);
            cells.Cell(8).Value = PaymentLabel(ticket.PaymentStatus, turkish);
            cells.Cell(9).Value = ticket.Channel == TicketChannel.Web ? (turkish ? "Web sitesi" : "Website") : (turkish ? "Yönetici" : "Admin");
            cells.Cell(10).Value = ticket.CustomerName;
            cells.Cell(11).Value = ticket.CustomerEmail;
            cells.Cell(12).Value = ticket.CustomerPhone ?? string.Empty;
            cells.Cell(13).Value = ticket.GuestCount;
            cells.Cell(14).Value = string.Join(", ", passengers.Select(passenger => $"{passenger.FirstName} {passenger.LastName}".Trim()));
            cells.Cell(15).Value = string.Join(", ", passengers.Select(passenger => passenger.TicketType).Where(type => !string.IsNullOrWhiteSpace(type)));
            cells.Cell(16).Value = ticket.Amount;
            cells.Cell(17).Value = ticket.Currency;
            cells.Cell(18).Value = ticket.WantsEInvoice ? (turkish ? "Evet" : "Yes") : (turkish ? "Hayır" : "No");
            cells.Cell(19).Value = ticket.InvoiceType switch
            {
                TicketInvoiceType.Individual => turkish ? "Bireysel" : "Individual",
                TicketInvoiceType.Corporate => turkish ? "Kurumsal" : "Corporate",
                _ => string.Empty,
            };
            cells.Cell(20).Value = ticket.InvoiceTitle ?? string.Empty;
            cells.Cell(21).Value = ticket.InvoiceTaxNumber ?? string.Empty;
            cells.Cell(22).Value = ticket.InvoiceTaxOffice ?? string.Empty;
            cells.Cell(23).Value = ticket.InvoiceEmail ?? string.Empty;
            cells.Cell(24).Value = ticket.InvoiceAddress ?? string.Empty;
            if (cancellation is not null)
            {
                cells.Cell(25).Value = actor?.Name ?? (turkish ? "Kullanıcı bulunamadı" : "User not found");
                cells.Cell(26).Value = actor?.Email ?? string.Empty;
                cells.Cell(27).Value = cancellation.Reason;
                cells.Cell(28).Value = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(cancellation.RequestedAtUtc, DateTimeKind.Utc), zone);
            }
            row++;
        }

        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#0B2A4A");
        sheet.SheetView.FreezeRows(1);
        sheet.Column(2).Style.DateFormat.Format = "dd.MM.yyyy HH:mm";
        sheet.Column(5).Style.DateFormat.Format = "dd.MM.yyyy";
        sheet.Column(16).Style.NumberFormat.Format = "#,##0.00";
        sheet.Column(28).Style.DateFormat.Format = "dd.MM.yyyy HH:mm";
        sheet.Range(1, 1, Math.Max(1, row - 1), headers.Length).SetAutoFilter();
        sheet.Columns(1, headers.Length).AdjustToContents(8.0, 45.0);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static string StatusLabel(TicketStatus status, bool turkish) => status switch
    {
        TicketStatus.Pending => turkish ? "Bekliyor" : "Pending",
        TicketStatus.Confirmed => turkish ? "Onaylandı" : "Confirmed",
        TicketStatus.Cancelled => turkish ? "İptal" : "Cancelled",
        TicketStatus.Used => turkish ? "Kullanıldı" : "Used",
        _ => status.ToString(),
    };

    private static string PaymentLabel(TicketPaymentStatus status, bool turkish) => status switch
    {
        TicketPaymentStatus.NotRequired => turkish ? "Manuel" : "Manual",
        TicketPaymentStatus.Pending => turkish ? "Bekliyor" : "Pending",
        TicketPaymentStatus.Processing => turkish ? "İşleniyor" : "Processing",
        TicketPaymentStatus.Paid => turkish ? "Ödendi" : "Paid",
        TicketPaymentStatus.Failed => turkish ? "Başarısız" : "Failed",
        TicketPaymentStatus.Refunded => turkish ? "İade edildi" : "Refunded",
        TicketPaymentStatus.ReviewRequired => turkish ? "Kontrol gerekli" : "Review required",
        _ => status.ToString(),
    };
}
