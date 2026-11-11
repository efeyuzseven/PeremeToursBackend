using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tickets;

namespace PeremeTours.UnitTests;

// The admin sales spreadsheet filters by Istanbul sale month, status and search like the ticket screen.
public sealed class TicketExportTests : IDisposable
{
    private static readonly DateOnly November = new(2026, 11, 1);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly Guid _actor = Guid.NewGuid();

    public TicketExportTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, TicketCancellationTests.SqliteTestModel>().Options);
        _db.Database.EnsureCreated();
        // 21:30 UTC on 31 October is 00:30 on 1 November in Istanbul, so it is a November sale.
        var early = Ticket("PRM-NOV-EARLY", "Işıl Çelik", new DateTimeOffset(2026, 10, 31, 21, 30, 0, TimeSpan.Zero), TicketStatus.Confirmed);
        early.WantsEInvoice = true; early.InvoiceType = TicketInvoiceType.Corporate; early.InvoiceTitle = "Örnek Ltd."; early.InvoiceTaxNumber = "0123456789";
        var october = Ticket("PRM-OCT-LATE", "Ali Veli", new DateTimeOffset(2026, 10, 31, 20, 30, 0, TimeSpan.Zero), TicketStatus.Confirmed);
        var refunded = Ticket("PRM-NOV-REFUND", "Deniz Kaya", new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero), TicketStatus.Cancelled);
        refunded.PaymentStatus = TicketPaymentStatus.Refunded;
        _db.TourTickets.AddRange(early, october, refunded);
        _db.Users.Add(new UserAccount { Id = _actor, Email = "ayse@example.test", NormalizedEmail = "AYSE@EXAMPLE.TEST", FirstName = "Ayşe",
            LastName = "Yılmaz", PasswordHash = "hash", Role = "Admin", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        _db.TicketCancellations.Add(new TicketCancellation { TicketId = refunded.Id, ActorUserId = _actor, Reason = "Müşteri talebi", Amount = 350,
            Status = TicketCancellationStatus.Completed, RequestedAtUtc = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 11, 16, 9, 1, 0, DateTimeKind.Utc), UpdatedAtUtc = new DateTime(2026, 11, 16, 9, 1, 0, DateTimeKind.Utc) });
        _db.SaveChanges(); _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task MonthUsesIstanbulSaleDateAndRowsCarryInvoiceAndRefundDetails()
    {
        var rows = await ExportAsync(new TicketExportQuery(November, null, null, true));
        Assert.Equal(["PRM-NOV-REFUND", "PRM-NOV-EARLY"], rows.Select(row => row["Bilet No"]));
        var early = rows.Single(row => row["Bilet No"] == "PRM-NOV-EARLY");
        Assert.Equal(new DateTime(2026, 11, 1, 0, 30, 0).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture), early["Satış Tarihi"]);
        Assert.Equal("Evet", early["Fatura İsteği"]);
        Assert.Equal("Kurumsal", early["Fatura Tipi"]);
        Assert.Equal("0123456789", early["T.C. / Vergi No"]);
        Assert.Equal("Demo Guest", early["Yolcular"]);
        var refund = rows.Single(row => row["Bilet No"] == "PRM-NOV-REFUND");
        Assert.Equal("İptal", refund["Durum"]);
        Assert.Equal("İade edildi", refund["Ödeme"]);
        Assert.Equal("Ayşe Yılmaz", refund["İadeyi Yapan"]);
        Assert.Equal("Müşteri talebi", refund["İade Nedeni"]);
        Assert.DoesNotContain(rows.SelectMany(row => row.Values), value => value.Contains("12345678901", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusAndTurkishSearchNarrowTheRows()
    {
        Assert.Equal(["PRM-NOV-EARLY"], (await ExportAsync(new TicketExportQuery(November, TicketStatus.Confirmed, null, true))).Select(row => row["Bilet No"]));
        Assert.Equal(["PRM-NOV-EARLY"], (await ExportAsync(new TicketExportQuery(null, null, "IŞIL", true))).Select(row => row["Bilet No"]));
        Assert.Equal(3, (await ExportAsync(new TicketExportQuery(null, null, null, false))).Count);
    }

    private async Task<List<Dictionary<string, string>>> ExportAsync(TicketExportQuery query)
    {
        var content = await new TicketService(_db).ExportAsync(query, CancellationToken.None);
        using var workbook = new XLWorkbook(new MemoryStream(content));
        var sheet = workbook.Worksheet(1);
        var lastColumn = sheet.Row(1).LastCellUsed()!.Address.ColumnNumber;
        var headers = Enumerable.Range(1, lastColumn).Select(column => sheet.Cell(1, column).GetString()).ToList();
        var lastRow = sheet.LastRowUsed()!.RowNumber();
        return Enumerable.Range(2, Math.Max(0, lastRow - 1)).Select(row => headers.Select((header, index) => (header, cell: sheet.Cell(row, index + 1)))
            .ToDictionary(item => item.header, item => item.cell.DataType == XLDataType.DateTime
                ? item.cell.GetDateTime().ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : item.cell.GetString())).ToList();
    }

    private static TourTicket Ticket(string code, string customer, DateTimeOffset createdAt, TicketStatus status) => new()
    {
        Id = Guid.NewGuid(), TicketCode = code, TourName = "Sunset", CustomerName = customer, CustomerEmail = $"{code.ToLowerInvariant()}@example.test",
        GuestCount = 1, Amount = 350, Currency = "TRY", Status = status, PaymentStatus = TicketPaymentStatus.Paid, TicketingStatus = TicketingStatus.Issued,
        TourDate = new DateOnly(2026, 12, 1), DepartureTime = new TimeOnly(18, 0), Channel = TicketChannel.Web, CreatedAtUtc = createdAt, UpdatedAtUtc = createdAt,
        Passengers = [new TourPassenger { Id = Guid.NewGuid(), FirstName = "Demo", LastName = "Guest", Gender = "male", Nationality = "TR", IdentityNumber = "12345678901" }],
    };

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
