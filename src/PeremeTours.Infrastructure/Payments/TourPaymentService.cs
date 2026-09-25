using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Payments;

internal sealed class TourPaymentService(
    PeremeToursDbContext dbContext,
    ITourCatalogService tourCatalogService,
    IZiraatPosGateway gateway,
    IOptions<ZiraatPosOptions> options,
    ILogger<TourPaymentService> logger
) : ITourPaymentService
{
    private static readonly Action<ILogger, string, string, Guid, decimal, Exception?> LogInitialized =
        LoggerMessage.Define<string, string, Guid, decimal>(
            LogLevel.Information,
            new EventId(3101, nameof(LogInitialized)),
            "Tour payment initialized. Application={Application} PaymentProvider=Ziraat OrderId={OrderId} TicketId={TicketId} Amount={Amount} Currency=TRY"
        );
    private static readonly Action<ILogger, string, Exception?> LogInvalidCallbackHash =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(3102, nameof(LogInvalidCallbackHash)),
            "Rejected Ziraat callback with invalid hash. Application={Application} PaymentProvider=Ziraat"
        );
    private static readonly Action<ILogger, string, string, string?, Exception?> LogDeclined =
        LoggerMessage.Define<string, string, string?>(
            LogLevel.Warning,
            new EventId(3103, nameof(LogDeclined)),
            "Tour payment declined. Application={Application} PaymentProvider=Ziraat OrderId={OrderId} BankCode={BankCode}"
        );
    private static readonly Action<ILogger, string, string, Guid, decimal, string?, Exception?> LogCompleted =
        LoggerMessage.Define<string, string, Guid, decimal, string?>(
            LogLevel.Information,
            new EventId(3104, nameof(LogCompleted)),
            "Tour payment completed. Application={Application} PaymentProvider=Ziraat OrderId={OrderId} TicketId={TicketId} Amount={Amount} Currency=TRY HostReference={HostReference}"
        );
    private readonly ZiraatPosOptions _options = options.Value;

    public async Task<StartTourPaymentResult> StartAsync(
        StartTourPaymentCommand command,
        CancellationToken cancellationToken
    )
    {
        if (!_options.Enabled)
        {
            throw new PaymentConfigurationException("Ödeme sistemi henüz etkin değil.");
        }
        Validate(command);

        var availability = await tourCatalogService.GetAvailabilityAsync(
            command.ExternalTourId,
            command.ExternalDeparturePortId,
            2,
            cancellationToken
        ) ?? throw new PaymentValidationException("Seçilen tur bulunamadı.");

        var departure = availability.Departures.SingleOrDefault(item =>
            item.ExternalId == command.ExternalDepartureId
            && item.ExternalTripId == command.ExternalTripId
            && item.ExternalPortId == command.ExternalDeparturePortId
        ) ?? throw new PaymentValidationException("Seçilen sefer artık kullanılamıyor.");
        if (departure.Date != command.TourDate)
        {
            throw new PaymentValidationException("Sefer tarihi değişmiş. Lütfen yeniden seçim yapın.");
        }

        var price = availability.Prices.SingleOrDefault(item =>
            item.ExternalPriceId == command.ExternalPriceId
        ) ?? throw new PaymentValidationException("Seçilen fiyat artık kullanılamıyor.");
        if (price.Amount <= 0
            || !(string.Equals(price.Currency, "TRY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(price.Currency, "TL", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PaymentValidationException("Bu sefer için desteklenen bir TL fiyatı bulunamadı.");
        }

        var now = DateTimeOffset.UtcNow;
        var amount = decimal.Round(
            price.Amount * command.GuestCount,
            2,
            MidpointRounding.AwayFromZero
        );
        var ticket = new TourTicket
        {
            Id = Guid.NewGuid(),
            TicketCode = CreateOrderId(now),
            TourName = availability.TourName.Trim(),
            TourDate = command.TourDate,
            DepartureTime = departure.Time ?? TimeOnly.MinValue,
            CustomerName = command.CustomerName.Trim(),
            CustomerEmail = command.CustomerEmail.Trim().ToLowerInvariant(),
            CustomerPhone = NormalizePhone(command.CustomerPhone),
            GuestCount = command.GuestCount,
            Amount = amount,
            Currency = "TRY",
            Status = TicketStatus.Pending,
            Channel = TicketChannel.Web,
            PaymentStatus = TicketPaymentStatus.Pending,
            ExternalTourId = command.ExternalTourId,
            ExternalDeparturePortId = command.ExternalDeparturePortId,
            ExternalDepartureId = command.ExternalDepartureId,
            ExternalTripId = command.ExternalTripId,
            ExternalPriceId = command.ExternalPriceId,
            PaymentProvider = "Ziraat",
            UserId = command.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        dbContext.TourTickets.Add(ticket);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var html = await gateway.StartThreeDSecureAsync(
                new ZiraatPaymentRequest(
                    ticket.TicketCode,
                    ticket.Amount,
                    command.Language,
                    command.Card
                ),
                cancellationToken
            );
            LogInitialized(
                logger,
                _options.ApplicationName,
                ticket.TicketCode,
                ticket.Id,
                ticket.Amount,
                null
            );
            return new StartTourPaymentResult(
                ticket.Id,
                ticket.TicketCode,
                ticket.Amount,
                ticket.Currency,
                html
            );
        }
        catch
        {
            ticket.PaymentStatus = TicketPaymentStatus.Failed;
            ticket.PaymentFailureCode = "GATEWAY_START_FAILED";
            ticket.PaymentFailureMessage = "Banka doğrulaması başlatılamadı.";
            ticket.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<CompleteTourPaymentResult> CompleteAsync(
        IReadOnlyDictionary<string, string> formFields,
        CancellationToken cancellationToken
    )
    {
        if (!gateway.VerifyCallback(formFields))
        {
            LogInvalidCallbackHash(
                logger,
                _options.ApplicationName,
                null
            );
            return Failure(null, "Ödeme doğrulanamadı.");
        }

        var orderId = Get(formFields, "oid");
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return Failure(null, "Ödeme sipariş numarası bulunamadı.");
        }

        var ticket = await dbContext.TourTickets
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.TicketCode == orderId, cancellationToken);
        if (ticket is null)
        {
            return Failure(null, "Rezervasyon bulunamadı.");
        }
        if (ticket.PaymentStatus == TicketPaymentStatus.Paid)
        {
            return Success(ticket.TicketCode);
        }

        var validationError = ValidateCallback(ticket, formFields);
        if (validationError is not null)
        {
            await MarkFailedAsync(ticket.Id, "CALLBACK_VALIDATION_FAILED", validationError, cancellationToken);
            return Failure(ticket.TicketCode, validationError);
        }

        var claimed = await dbContext.TourTickets
            .Where(item =>
                item.Id == ticket.Id
                && item.PaymentStatus == TicketPaymentStatus.Pending
            )
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.PaymentStatus, TicketPaymentStatus.Processing)
                    .SetProperty(item => item.UpdatedAtUtc, DateTimeOffset.UtcNow),
                cancellationToken
            );
        if (claimed == 0)
        {
            var current = await dbContext.TourTickets
                .AsNoTracking()
                .Where(item => item.Id == ticket.Id)
                .Select(item => item.PaymentStatus)
                .SingleAsync(cancellationToken);
            return current == TicketPaymentStatus.Paid
                ? Success(ticket.TicketCode)
                : Failure(ticket.TicketCode, "Ödeme işlemi zaten işleniyor veya tamamlanamadı.");
        }

        try
        {
            var result = await gateway.FinalizeAsync(
                ticket.TicketCode,
                ticket.Amount,
                formFields,
                cancellationToken
            );
            var tracked = await dbContext.TourTickets.SingleAsync(
                item => item.Id == ticket.Id,
                cancellationToken
            );
            tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
            tracked.BankAuthCode = Limit(result.AuthCode, 64);
            tracked.BankHostReference = Limit(result.HostReference, 128);

            if (!result.IsApproved)
            {
                tracked.PaymentStatus = TicketPaymentStatus.Failed;
                tracked.PaymentFailureCode = Limit(result.ErrorCode ?? "BANK_DECLINED", 64);
                tracked.PaymentFailureMessage = Limit(
                    string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "Ödeme banka tarafından onaylanmadı."
                        : result.ErrorMessage,
                    500
                );
                await dbContext.SaveChangesAsync(cancellationToken);
                LogDeclined(
                    logger,
                    _options.ApplicationName,
                    ticket.TicketCode,
                    result.ErrorCode,
                    null
                );
                return Failure(ticket.TicketCode, "Ödeme banka tarafından onaylanmadı.");
            }

            tracked.PaymentStatus = TicketPaymentStatus.Paid;
            tracked.Status = TicketStatus.Confirmed;
            tracked.PaidAtUtc = DateTimeOffset.UtcNow;
            tracked.PaymentFailureCode = null;
            tracked.PaymentFailureMessage = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            LogCompleted(
                logger,
                _options.ApplicationName,
                ticket.TicketCode,
                ticket.Id,
                ticket.Amount,
                result.HostReference,
                null
            );
            return Success(ticket.TicketCode);
        }
        catch (PaymentGatewayException)
        {
            await ReturnToPendingAsync(ticket.Id);
            throw;
        }
        catch
        {
            await ReturnToPendingAsync(ticket.Id);
            throw;
        }
    }

    private static void Validate(StartTourPaymentCommand command)
    {
        if (command.ExternalTourId <= 0
            || command.ExternalDeparturePortId <= 0
            || command.ExternalDepartureId <= 0
            || command.ExternalTripId <= 0
            || command.ExternalPriceId <= 0)
        {
            throw new PaymentValidationException("Tur veya sefer seçimi geçersiz.");
        }
        if (command.TourDate < DateOnly.FromDateTime(DateTime.UtcNow))
        {
            throw new PaymentValidationException("Geçmiş tarihli bir tur satın alınamaz.");
        }
        if (command.GuestCount is < 1 or > 12)
        {
            throw new PaymentValidationException("Misafir sayısı 1 ile 12 arasında olmalıdır.");
        }
        if (string.IsNullOrWhiteSpace(command.CustomerName) || command.CustomerName.Trim().Length > 160)
        {
            throw new PaymentValidationException("Ad soyad bilgisi geçersiz.");
        }
        try
        {
            _ = new MailAddress(command.CustomerEmail);
        }
        catch (FormatException)
        {
            throw new PaymentValidationException("E-posta adresi geçersiz.");
        }

        var cardNumber = DigitsOnly(command.Card.Number);
        var securityCode = DigitsOnly(command.Card.SecurityCode);
        if (string.IsNullOrWhiteSpace(command.Card.HolderName)
            || command.Card.HolderName.Trim().Length > 160
            || cardNumber.Length is < 13 or > 19
            || !PassesLuhn(cardNumber)
            || securityCode.Length is < 3 or > 4
            || command.Card.ExpiryMonth is < 1 or > 12
            || command.Card.ExpiryYear < DateTime.UtcNow.Year
            || command.Card.ExpiryYear > DateTime.UtcNow.Year + 20
            || (command.Card.ExpiryYear == DateTime.UtcNow.Year
                && command.Card.ExpiryMonth < DateTime.UtcNow.Month))
        {
            throw new PaymentValidationException("Kart bilgileri geçersiz.");
        }
    }

    private string? ValidateCallback(
        TourTicket ticket,
        IReadOnlyDictionary<string, string> fields
    )
    {
        if (!string.Equals(Get(fields, "hashAlgorithm"), "ver3", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Get(fields, "clientid"), _options.MerchantId, StringComparison.Ordinal)
            || !string.Equals(Get(fields, "currency"), "949", StringComparison.Ordinal)
            || !string.Equals(Get(fields, "mdStatus"), "1", StringComparison.Ordinal)
            || string.Equals(Get(fields, "Response"), "Error", StringComparison.OrdinalIgnoreCase))
        {
            return "3D Secure doğrulaması başarısız oldu.";
        }

        if (!decimal.TryParse(
                Get(fields, "amount"),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var callbackAmount
            )
            || callbackAmount != ticket.Amount)
        {
            return "Ödeme tutarı doğrulanamadı.";
        }

        return null;
    }

    private async Task MarkFailedAsync(
        Guid ticketId,
        string code,
        string message,
        CancellationToken cancellationToken
    ) => await dbContext.TourTickets
        .Where(item => item.Id == ticketId && item.PaymentStatus != TicketPaymentStatus.Paid)
        .ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.PaymentStatus, TicketPaymentStatus.Failed)
                .SetProperty(item => item.PaymentFailureCode, Limit(code, 64))
                .SetProperty(item => item.PaymentFailureMessage, Limit(message, 500))
                .SetProperty(item => item.UpdatedAtUtc, DateTimeOffset.UtcNow),
            cancellationToken
        );

    private async Task ReturnToPendingAsync(Guid ticketId) => await dbContext.TourTickets
        .Where(item => item.Id == ticketId && item.PaymentStatus == TicketPaymentStatus.Processing)
        .ExecuteUpdateAsync(
            setters => setters
                .SetProperty(item => item.PaymentStatus, TicketPaymentStatus.Pending)
                .SetProperty(item => item.UpdatedAtUtc, DateTimeOffset.UtcNow),
            CancellationToken.None
        );

    private string CreateOrderId(DateTimeOffset now)
    {
        var prefix = string.IsNullOrWhiteSpace(_options.OrderPrefix)
            ? "PRM"
            : new string(_options.OrderPrefix.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "PRM";
        }
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        return $"{prefix}-{now:yyyyMMdd}-{random}";
    }

    private static CompleteTourPaymentResult Success(string ticketCode) => new(
        true,
        ticketCode,
        "Ödeme başarıyla tamamlandı."
    );

    private static CompleteTourPaymentResult Failure(string? ticketCode, string message) => new(
        false,
        ticketCode,
        message
    );

    private static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalized = new string(value.Where(character => char.IsAsciiDigit(character) || character == '+').ToArray());
        return Limit(normalized, 32);
    }

    private static string DigitsOnly(string value) => string.Concat(value.Where(char.IsAsciiDigit));

    private static bool PassesLuhn(string number)
    {
        var sum = 0;
        var doubleDigit = false;
        for (var index = number.Length - 1; index >= 0; index--)
        {
            var digit = number[index] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    private static string Get(IReadOnlyDictionary<string, string> fields, string name) =>
        ZiraatPosHash.TryGet(fields, name, out var value) ? value : string.Empty;

    private static string? Limit(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];
}
