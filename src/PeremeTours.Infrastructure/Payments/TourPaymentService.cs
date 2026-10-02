using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PeremeTours.Application.Payments;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.Infrastructure.Payments;

internal sealed class TourPaymentService(
    PeremeToursDbContext dbContext, ITourBookingService bookingService,
    IZiraatPosGateway gateway, IEasyTicketSalesGateway salesGateway,
    IOptions<ZiraatPosOptions> options, IOptions<EasyTicketOptions> easyTicketOptions,
    TimeProvider timeProvider, ILogger<TourPaymentService> logger
) : ITourPaymentService
{
    private static readonly TimeZoneInfo IstanbulTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly Action<ILogger, string, string, string, Exception?> LogState = LoggerMessage.Define<string, string, string>(
        LogLevel.Information, new EventId(3101, nameof(LogState)),
        "Tour payment state. Application={Application} PaymentProvider=Ziraat OrderId={OrderId} State={State}");
    private readonly ZiraatPosOptions _options = options.Value;
    private readonly EasyTicketOptions _easyTicket = easyTicketOptions.Value;

    public PaymentAvailability GetAvailability() => new(
        _options.IsConfigured && !string.IsNullOrWhiteSpace(_easyTicket.ApiKey)
            && Uri.TryCreate(_easyTicket.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
        "Ziraat");

    public async Task<TourPaymentStatus?> GetStatusAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty) return null;
        var ticket = await dbContext.TourTickets.AsNoTracking().Include(item => item.Passengers).Include(item => item.PaymentEmail)
            .SingleOrDefaultAsync(item => item.PaymentAttemptId == attemptId, cancellationToken);
        return ticket is null ? null : new TourPaymentStatus(ticket.TicketCode, ticket.Amount, ticket.Currency,
            ticket.PaymentStatus.ToString(), ticket.TicketingStatus.ToString(),
            ticket.TicketingStatus == TicketingStatus.Issued
                ? ticket.Passengers.OrderBy(item => item.Sequence).Select(item => new IssuedTourTicket(item.Pnr, item.ExternalTicketGuid)).ToArray()
                : [], ticket.PaymentEmail?.Status.ToString());
    }

    public async Task<StartTourPaymentResult> StartAsync(StartTourPaymentCommand command, CancellationToken cancellationToken)
    {
        if (!GetAvailability().Enabled) throw new PaymentConfigurationException("Ödeme sistemi şu anda kullanılamıyor.");
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, IstanbulTimeZone).DateTime);
        PaymentValidation.Validate(command, today);
        if (await dbContext.TourTickets.AnyAsync(item => item.PaymentAttemptId == command.AttemptId, cancellationToken))
            throw new PaymentConflictException("Bu ödeme daha önce başlatıldı. Mevcut işlemin sonucunu kontrol edin.");

        TourQuote quote;
        try
        {
            quote = await bookingService.QuoteAsync(new TourQuoteCommand(command.ExternalTourId,
                command.ExternalDeparturePortId, command.ExternalDepartureId, command.TourDate, command.Tickets), cancellationToken);
        }
        catch (TourBookingValidationException exception) { throw new PaymentValidationException(exception.Message); }
        if (quote.Amount <= 0) throw new PaymentValidationException("Ödenecek tutar geçersiz.");
        if (quote.Amount != command.ExpectedAmount)
            throw new PaymentConflictException("Fiyat değişti. Bilgileri düzenleyip yeni tutarı kontrol edin; ödeme başlatılmadı.");
        if (command.Passengers.Count != quote.GuestCount
            || command.Passengers.GroupBy(item => item.ExternalPriceId).Count() != quote.Tickets.Count
            || quote.Tickets.Any(line => command.Passengers.Count(item => item.ExternalPriceId == line.ExternalPriceId) != line.Quantity))
            throw new PaymentValidationException("Yolcular seçilen bilet tipleriyle eşleşmiyor.");

        var ticket = new TourTicket
        {
            Id = Guid.NewGuid(), TicketCode = CreateOrderId(now), PaymentAttemptId = command.AttemptId,
            TourName = quote.TourName.Trim(), TourDate = quote.TourDate, DepartureTime = quote.DepartureTime,
            CustomerName = command.CustomerName.Trim(), CustomerEmail = command.CustomerEmail.Trim().ToLowerInvariant(),
            CustomerLanguage = command.Language == "en" ? "en" : "tr", DeparturePortName = quote.PortName,
            CustomerPhone = command.CustomerPhone?.Trim(), GuestCount = quote.GuestCount,
            Amount = quote.Amount, Currency = "TRY", Status = TicketStatus.Pending, Channel = TicketChannel.Web,
            PaymentStatus = TicketPaymentStatus.Pending, TicketingStatus = TicketingStatus.Pending,
            ExternalTourId = quote.ExternalTourId, ExternalDeparturePortId = quote.ExternalDeparturePortId,
            ExternalDepartureId = quote.ExternalDepartureId,
            ExternalTripId = quote.ExternalTripId > 0 ? quote.ExternalTripId : quote.ExternalDepartureId,
            PaymentProvider = "Ziraat", UserId = command.UserId, CreatedAtUtc = now, UpdatedAtUtc = now,
            Passengers = command.Passengers.Select((item, index) => new TourPassenger
            {
                Id = Guid.NewGuid(), Sequence = index,
                ExternalPriceId = item.ExternalPriceId,
                UnitAmount = quote.Tickets.Single(line => line.ExternalPriceId == item.ExternalPriceId).UnitAmount,
                FirstName = item.FirstName.Trim(), LastName = item.LastName.Trim(), Gender = item.Gender,
                Nationality = item.Nationality, IdentityNumber = item.IdentityNumber.Trim(), BirthDate = item.BirthDate,
            }).ToList(),
        };
        dbContext.TourTickets.Add(ticket);
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new PaymentConflictException("Bu ödeme daha önce başlatıldı. Mevcut işlemin sonucunu kontrol edin.");
        }
        try
        {
            var html = await gateway.StartThreeDSecureAsync(new ZiraatPaymentRequest(ticket.TicketCode, ticket.Amount,
                command.Language, command.Card), cancellationToken);
            LogState(logger, _options.ApplicationName, ticket.TicketCode, "Initialized", null);
            return new StartTourPaymentResult(ticket.Id, ticket.TicketCode, ticket.Amount, ticket.Currency, html);
        }
        catch (Exception exception) when (exception is PaymentGatewayException or HttpRequestException or OperationCanceledException)
        {
            // No Auth has been sent at this stage. Card data is never persisted.
            ticket.PaymentStatus = TicketPaymentStatus.Failed;
            ticket.PaymentFailureCode = "GATEWAY_START_FAILED";
            ticket.PaymentFailureMessage = "Banka doğrulaması başlatılamadı.";
            ticket.UpdatedAtUtc = timeProvider.GetUtcNow();
            PaymentDiagnostics.Add(dbContext, ticket.Id, TicketErrorStage.Payment, "GATEWAY_START_FAILED",
                ticket.PaymentFailureMessage, ticket.UpdatedAtUtc);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw new PaymentGatewayException("Banka doğrulaması başlatılamadı.");
        }
    }

    public async Task<CompleteTourPaymentResult> CompleteAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        if (!gateway.VerifyCallback(fields)) return Failure(null, "Ödeme doğrulanamadı.");
        var orderId = Get(fields, "oid");
        if (string.IsNullOrWhiteSpace(orderId)) return Failure(null, "Ödeme siparişi bulunamadı.");
        var ticket = await dbContext.TourTickets.AsNoTracking().SingleOrDefaultAsync(item => item.TicketCode == orderId, cancellationToken);
        if (ticket is null) return Failure(null, "Rezervasyon bulunamadı.");
        if (ticket.PaymentStatus == TicketPaymentStatus.Paid) return PaidResult(ticket);
        if (ticket.PaymentStatus != TicketPaymentStatus.Pending)
            return Failure(ticket.TicketCode, "Mevcut işlemin sonucu kontrol ediliyor. Tekrar ödeme yapmayın.");

        var validationError = ValidateCallback(ticket, fields);
        if (validationError is not null)
        {
            var invalidated = await dbContext.TourTickets.Where(item => item.Id == ticket.Id && item.PaymentStatus == TicketPaymentStatus.Pending)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PaymentStatus, TicketPaymentStatus.Failed)
                    .SetProperty(item => item.PaymentFailureCode, "CALLBACK_VALIDATION_FAILED")
                    .SetProperty(item => item.PaymentFailureMessage, validationError)
                    .SetProperty(item => item.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
            if (invalidated > 0)
            {
                PaymentDiagnostics.Add(dbContext, ticket.Id, TicketErrorStage.Payment,
                    "CALLBACK_VALIDATION_FAILED", validationError, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
            return Failure(ticket.TicketCode, validationError);
        }
        var claimed = await dbContext.TourTickets.Where(item => item.Id == ticket.Id && item.PaymentStatus == TicketPaymentStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PaymentStatus, TicketPaymentStatus.Processing)
                .SetProperty(item => item.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
        if (claimed == 0) return Failure(ticket.TicketCode, "Ödeme sonucu kontrol ediliyor. Tekrar ödeme yapmayın.");

        // Finish once claimed, even if the customer's browser disconnects. HTTP clients have bounded timeouts.
        try
        {
            var result = await gateway.FinalizeAsync(ticket.TicketCode, ticket.Amount, fields, CancellationToken.None);
            var tracked = await dbContext.TourTickets.Include(item => item.Passengers).SingleAsync(item => item.Id == ticket.Id, CancellationToken.None);
            tracked.UpdatedAtUtc = timeProvider.GetUtcNow();
            tracked.BankAuthCode = Limit(result.AuthCode, 64);
            tracked.BankHostReference = Limit(result.HostReference, 128);
            if (!result.IsApproved)
            {
                tracked.PaymentStatus = TicketPaymentStatus.Failed;
                tracked.PaymentFailureCode = PaymentDiagnostics.SafeCode(result.ErrorCode, "BANK_DECLINED");
                tracked.PaymentFailureMessage = PaymentDiagnostics.BankMessage(result);
                PaymentDiagnostics.Add(dbContext, tracked.Id, TicketErrorStage.Payment,
                    tracked.PaymentFailureCode, tracked.PaymentFailureMessage, tracked.UpdatedAtUtc, result.ErrorDetailCode);
                await dbContext.SaveChangesAsync(CancellationToken.None);
                LogState(logger, _options.ApplicationName, ticket.TicketCode, "Declined", null);
                return Failure(ticket.TicketCode, "Ödeme banka tarafından onaylanmadı.");
            }
            tracked.PaymentStatus = TicketPaymentStatus.Paid;
            tracked.PaidAtUtc = timeProvider.GetUtcNow();
            tracked.PaymentFailureCode = null;
            tracked.PaymentFailureMessage = null;
            // The outbox entry and Paid state commit together; duplicate callbacks cannot queue another mail.
            dbContext.PaymentEmails.Add(new PaymentEmail
            {
                TicketId = tracked.Id, Status = PaymentEmailStatus.Queued,
                CreatedAtUtc = tracked.PaidAtUtc.Value.UtcDateTime,
                NextAttemptAtUtc = tracked.PaidAtUtc.Value.UtcDateTime,
            });
            // Persist the bank result BEFORE ticket issuance; a ticket failure must never allow another charge.
            await dbContext.SaveChangesAsync(CancellationToken.None);
            LogState(logger, _options.ApplicationName, ticket.TicketCode, "Paid", null);
            await IssueTicketsAsync(tracked);
            return PaidResult(tracked);
        }
        catch
        {
            // Auth may have succeeded despite a timeout. Never reset to Pending or resubmit Auth automatically.
            // Discard tracked changes before recording failures; do not accidentally overwrite the persisted bank state.
            dbContext.ChangeTracker.Clear();
            var uncertain = await dbContext.TourTickets.Where(item => item.Id == ticket.Id && item.PaymentStatus == TicketPaymentStatus.Processing)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PaymentStatus, TicketPaymentStatus.ReviewRequired)
                    .SetProperty(item => item.PaymentFailureCode, "BANK_RESULT_UNKNOWN")
                    .SetProperty(item => item.PaymentFailureMessage, "Banka sonucu kontrol edilmeli; tekrar tahsilat yapılmamalı.")
                    .SetProperty(item => item.UpdatedAtUtc, timeProvider.GetUtcNow()), CancellationToken.None);
            if (uncertain > 0)
            {
                PaymentDiagnostics.Add(dbContext, ticket.Id, TicketErrorStage.Payment, "BANK_RESULT_UNKNOWN",
                    "Banka sonucu belirsiz. Yeni tahsilat yapmayın; banka kaydını sipariş koduyla kontrol edin.", timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }
            else
            {
                var paid = await dbContext.TourTickets.AsNoTracking().SingleAsync(item => item.Id == ticket.Id, CancellationToken.None);
                if (paid.PaymentStatus == TicketPaymentStatus.Paid)
                {
                    var unfinished = await dbContext.TourTickets.Where(item => item.Id == ticket.Id
                            && (item.TicketingStatus == TicketingStatus.Pending || item.TicketingStatus == TicketingStatus.Processing))
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.TicketingStatus, TicketingStatus.ReviewRequired)
                            .SetProperty(item => item.TicketingFailureCode, "PROVIDER_RESULT_UNKNOWN")
                            .SetProperty(item => item.UpdatedAtUtc, timeProvider.GetUtcNow()), CancellationToken.None);
                    if (unfinished > 0)
                    {
                        PaymentDiagnostics.Add(dbContext, ticket.Id, TicketErrorStage.Ticketing, "PROVIDER_RESULT_UNKNOWN",
                            "Ödeme alındı; bilet kesim sonucu belirsiz. EasyTicket kaydı kontrol edilmeli. Yeni tahsilat yapmayın.", timeProvider.GetUtcNow());
                        await dbContext.SaveChangesAsync(CancellationToken.None);
                        paid.TicketingStatus = TicketingStatus.ReviewRequired;
                    }
                    return PaidResult(paid);
                }
            }
            LogState(logger, _options.ApplicationName, ticket.TicketCode, "ReviewRequired", null);
            return Failure(ticket.TicketCode, "Ödeme sonucu kontrol ediliyor. Tekrar ödeme yapmayın; destek ekibiyle iletişime geçin.");
        }
    }

    private async Task IssueTicketsAsync(TourTicket ticket)
    {
        var claimed = await dbContext.TourTickets.Where(item => item.Id == ticket.Id && item.PaymentStatus == TicketPaymentStatus.Paid
            && item.TicketingStatus == TicketingStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.TicketingStatus, TicketingStatus.Processing)
                .SetProperty(item => item.UpdatedAtUtc, timeProvider.GetUtcNow()), CancellationToken.None);
        if (claimed == 0) return;
        ticket.TicketingStatus = TicketingStatus.Processing;
        try
        {
            var sale = await salesGateway.IssueAsync(ticket, CancellationToken.None);
            ticket.ExternalVoucherGuid = Limit(sale.VoucherGuid, 128);
            var passengers = ticket.Passengers.OrderBy(item => item.Sequence).ToArray();
            for (var index = 0; index < Math.Min(passengers.Length, sale.Tickets.Count); index++)
            {
                passengers[index].ExternalTicketGuid = Limit(sale.Tickets[index].Guid, 128);
                passengers[index].Pnr = Limit(sale.Tickets[index].Pnr, 128);
            }
            ticket.TicketingStatus = sale.IsComplete ? TicketingStatus.Issued : TicketingStatus.ReviewRequired;
            ticket.Status = sale.IsComplete ? TicketStatus.Confirmed : TicketStatus.Pending;
            ticket.TicketingFailureCode = sale.IsComplete ? null : "PROVIDER_RESULT_INCOMPLETE";
        }
        catch (PaymentTicketingException)
        {
            ticket.TicketingStatus = TicketingStatus.ReviewRequired;
            ticket.TicketingFailureCode = "PROVIDER_RESULT_UNKNOWN";
        }
        ticket.UpdatedAtUtc = timeProvider.GetUtcNow();
        if (ticket.TicketingFailureCode is not null)
            PaymentDiagnostics.Add(dbContext, ticket.Id, TicketErrorStage.Ticketing, ticket.TicketingFailureCode,
                "Ödeme alındı; EasyTicket biletleri tam olarak doğrulanamadı. Yeni tahsilat yapmayın; sağlayıcı kaydı kontrol edilmeli.", ticket.UpdatedAtUtc);
        await dbContext.SaveChangesAsync(CancellationToken.None);
        LogState(logger, _options.ApplicationName, ticket.TicketCode, ticket.TicketingStatus.ToString(), null);
    }

    private string? ValidateCallback(TourTicket ticket, IReadOnlyDictionary<string, string> fields)
    {
        if (!string.Equals(Get(fields, "hashAlgorithm"), "ver3", StringComparison.OrdinalIgnoreCase)
            || Get(fields, "clientid") != _options.MerchantId || Get(fields, "currency") != "949"
            || Get(fields, "mdStatus") != "1" || string.Equals(Get(fields, "Response"), "Error", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Get(fields, "md")) || string.IsNullOrWhiteSpace(Get(fields, "xid"))
            || string.IsNullOrWhiteSpace(Get(fields, "eci")) || string.IsNullOrWhiteSpace(Get(fields, "cavv")))
            return "3D Secure doğrulaması başarısız oldu.";
        if (!decimal.TryParse(Get(fields, "amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || amount != ticket.Amount) return "Ödeme tutarı doğrulanamadı.";
        return null;
    }

    private string CreateOrderId(DateTimeOffset now)
    {
        var prefix = new string(_options.OrderPrefix.Where(char.IsAsciiLetterOrDigit).Take(6).ToArray()).ToUpperInvariant();
        if (string.IsNullOrEmpty(prefix)) prefix = "PRM";
        return $"{prefix}-{now:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(5))}";
    }

    private static CompleteTourPaymentResult PaidResult(TourTicket ticket) => new(true, ticket.TicketCode,
        ticket.TicketingStatus == TicketingStatus.Issued ? "Ödeme tamamlandı, biletleriniz oluşturuldu."
            : "Ödeme alındı. Biletleriniz kontrol ediliyor; tekrar ödeme yapmayın.");
    private static CompleteTourPaymentResult Failure(string? ticketCode, string message) => new(false, ticketCode, message);
    private static string Get(IReadOnlyDictionary<string, string> fields, string name) => ZiraatPosHash.TryGet(fields, name, out var value) ? value : string.Empty;
    private static string? Limit(string? value, int maximumLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];
}
