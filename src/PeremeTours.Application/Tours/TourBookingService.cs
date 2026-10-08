namespace PeremeTours.Application.Tours;

public sealed class TourBookingService(
    ITourCatalogService catalog,
    TimeProvider timeProvider
) : ITourBookingService
{
    private static readonly TimeZoneInfo IstanbulTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public async Task<TourQuote> QuoteAsync(
        TourQuoteCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExternalTourId <= 0 || command.ExternalDeparturePortId <= 0
            || command.ExternalDepartureId <= 0 || command.Tickets is null
            || command.Tickets.Count is < 1 or > 12
            || command.Tickets.Any(item => item is null || item.ExternalPriceId <= 0 || item.Quantity is < 1 or > 12)
            || command.Tickets.Sum(item => item.Quantity) > 12
            || command.Tickets.DistinctBy(item => item.ExternalPriceId).Count() != command.Tickets.Count)
        {
            throw new TourBookingValidationException("Bilet seçimini kontrol edin. En fazla 12 misafir seçilebilir.");
        }

        var ports = await catalog.ListPortsAsync(command.ExternalTourId, cancellationToken);
        var port = ports.SingleOrDefault(item => item.ExternalPortId == command.ExternalDeparturePortId)
            ?? throw new TourBookingValidationException("Seçilen kalkış noktası bu tur için kullanılamıyor.");
        var availability = await catalog.GetAvailabilityAsync(
            command.ExternalTourId,
            command.ExternalDeparturePortId,
            2,
            cancellationToken
        ) ?? throw new TourBookingValidationException("Seçilen tur artık kullanılamıyor.");
        var departure = availability.Departures.SingleOrDefault(item =>
            item.ExternalId == command.ExternalDepartureId
            && item.ExternalPortId == command.ExternalDeparturePortId
            && item.Date == command.TourDate
        );
        if (departure?.Time is not TimeOnly departureTime)
        {
            throw new TourBookingValidationException("Seçilen tarih veya sefer artık kullanılamıyor. Lütfen yeniden seçin.");
        }
        var now = timeProvider.GetUtcNow();
        var localDeparture = command.TourDate.ToDateTime(departureTime, DateTimeKind.Unspecified);
        if (TimeZoneInfo.ConvertTimeToUtc(localDeparture, IstanbulTimeZone) <= now.UtcDateTime)
        {
            throw new TourBookingValidationException("Geçmiş bir sefer seçilemez. Lütfen başka bir saat seçin.");
        }

        var lines = command.Tickets.Select(selection =>
        {
            var price = availability.Prices.SingleOrDefault(item => item.ExternalPriceId == selection.ExternalPriceId)
                ?? throw new TourBookingValidationException("Seçilen bilet tipi artık kullanılamıyor. Lütfen yeniden seçin.");
            if (price.Amount < 0 || !(string.Equals(price.Currency, "TRY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(price.Currency, "TL", StringComparison.OrdinalIgnoreCase)))
            {
                throw new TourBookingValidationException("Bu bilet tipi için desteklenen bir TL fiyatı bulunamadı.");
            }
            return new TourQuoteLine(
                price.ExternalPriceId,
                price.PassengerType,
                price.PassengerTypeEn,
                selection.Quantity,
                price.Amount,
                decimal.Round(price.Amount * selection.Quantity, 2, MidpointRounding.AwayFromZero)
            );
        }).ToArray();

        return new TourQuote(
            availability.ExternalTourId,
            availability.TourName,
            port.ExternalPortId,
            port.Name,
            departure.ExternalId,
            command.TourDate,
            departureTime,
            lines,
            lines.Sum(item => item.Quantity),
            lines.Sum(item => item.Amount),
            "TRY",
            now,
            departure.ExternalTripId > 0 ? departure.ExternalTripId : departure.ExternalId,
            availability.CategoryKey
        );
    }
}
