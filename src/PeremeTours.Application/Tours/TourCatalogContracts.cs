namespace PeremeTours.Application.Tours;

public static class TourCategoryKeys
{
    public const string Bosphorus = "bosphorus";
    public const string TurkishNight = "turkish-night";
    public const string Sunset = "sunset";
    public const string Daytime = "daytime";
}

public sealed record TourCatalogItem(
    int ExternalTourId,
    int ExternalCategoryId,
    string CategoryKey,
    string CategoryName,
    string Name
);

public sealed record TourPort(
    int ExternalPortId,
    string Name,
    int DisplayOrder
);

public sealed record TourPrice(
    int ExternalPriceId,
    int ExternalPassengerTypeId,
    string PassengerType,
    decimal Amount,
    string Currency,
    int TaxRate
);

public sealed record TourDeparture(
    int ExternalId,
    int ExternalTripId,
    int ExternalPortId,
    string PortName,
    DateOnly? Date,
    TimeOnly? Time
);

public sealed record TourAvailability(
    int ExternalTourId,
    string TourName,
    string CategoryName,
    int SaleType,
    IReadOnlyList<TourPrice> Prices,
    IReadOnlyList<TourDeparture> Departures
);

public interface ITourCatalogService
{
    Task<IReadOnlyList<TourCatalogItem>> ListAsync(
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<TourPort>> ListPortsAsync(
        int externalTourId,
        CancellationToken cancellationToken
    );

    Task<TourAvailability?> GetAvailabilityAsync(
        int externalTourId,
        int externalDeparturePortId,
        int saleType,
        CancellationToken cancellationToken
    );
}

public sealed class TourCatalogUnavailableException(
    string message,
    Exception? innerException = null
) : Exception(message, innerException);
