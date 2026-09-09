using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Tours;

namespace PeremeTours.Infrastructure.Tours;

public sealed class EasyTicketTourCatalogService(
    HttpClient httpClient,
    IOptions<EasyTicketOptions> options,
    IMemoryCache cache,
    ILogger<EasyTicketTourCatalogService> logger
) : ITourCatalogService
{
    private const string CatalogCacheKey = "easyticket:catalog:v1";
    private static readonly Action<ILogger, Exception?> LogCatalogRequestFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1001, nameof(LogCatalogRequestFailed)),
            "EasyTicket tour catalog request failed."
        );
    private static readonly Action<ILogger, int, Exception?> LogPortRequestFailed =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(1002, nameof(LogPortRequestFailed)),
            "EasyTicket port request failed for tour {ExternalTourId}."
        );
    private static readonly Action<ILogger, int, int, Exception?>
        LogAvailabilityRequestFailed = LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(1003, nameof(LogAvailabilityRequestFailed)),
            "EasyTicket availability request failed for tour {ExternalTourId} and port {ExternalPortId}."
        );
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly CategorySpecification[] SupportedCategories =
    [
        new(
            TourCategoryKeys.Bosphorus,
            "Boğaz Turu",
            1,
            ["boğaz", "bogaz", "bosphorus"]
        ),
        new(
            TourCategoryKeys.TurkishNight,
            "Türk Gecesi Dinner Cruise",
            2,
            ["türk gecesi", "turk gecesi", "turkish night", "dinner cruise"]
        ),
        new(TourCategoryKeys.Sunset, "Sunset", 7, ["sunset"]),
        new(
            TourCategoryKeys.Daytime,
            "DayTime",
            9,
            ["daytime", "day time", "gündüz", "gunduz"]
        ),
    ];

    private readonly EasyTicketOptions _options = options.Value;

    public async Task<IReadOnlyList<TourCatalogItem>> ListAsync(
        CancellationToken cancellationToken
    )
    {
        if (cache.TryGetValue(
            CatalogCacheKey,
            out IReadOnlyList<TourCatalogItem>? cached
        ) && cached is not null)
        {
            return cached;
        }

        EnsureConfigured();

        try
        {
            var upstreamCategories = await GetAsync<List<EasyTicketCategory>>(
                "/api/Data/kategoriler",
                cancellationToken
            ) ?? [];

            var categories = SupportedCategories
                .Select(specification => new ResolvedCategory(
                    specification,
                    ResolveCategory(upstreamCategories, specification)
                ))
                .ToArray();

            var tourTasks = categories.Select(async category =>
            {
                var tours = await GetAsync<List<EasyTicketTour>>(
                    $"/api/Data/turlar/{category.Category.Id}",
                    cancellationToken
                ) ?? [];

                return tours
                    .Where(tour => tour.Id > 0 && !string.IsNullOrWhiteSpace(tour.Tur_Adi))
                    .Select(tour => new TourCatalogItem(
                        tour.Id,
                        category.Category.Id,
                        category.Specification.Key,
                        category.Specification.DisplayName,
                        tour.Tur_Adi!.Trim()
                    ));
            });

            var groupedTours = await Task.WhenAll(tourTasks);
            IReadOnlyList<TourCatalogItem> result = groupedTours
                .SelectMany(items => items)
                .DistinctBy(item => item.ExternalTourId)
                .ToList();

            cache.Set(
                CatalogCacheKey,
                result,
                TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 1, 60))
            );
            return result;
        }
        catch (TourCatalogUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException
                or JsonException
                or NotSupportedException
                or TaskCanceledException
        )
        {
            LogCatalogRequestFailed(logger, exception);
            throw new TourCatalogUnavailableException(
                "Tur bilgileri şu anda alınamıyor.",
                exception
            );
        }
    }

    public async Task<IReadOnlyList<TourPort>> ListPortsAsync(
        int externalTourId,
        CancellationToken cancellationToken
    )
    {
        await EnsureSupportedTourAsync(externalTourId, cancellationToken);

        try
        {
            var ports = await GetAsync<List<EasyTicketTourPort>>(
                $"/api/Data/tur-limanlar/{externalTourId}",
                cancellationToken
            ) ?? [];

            return ports
                .Where(port => port.Liman_Id > 0)
                .OrderBy(port => port.Sira_No)
                .Select(port => new TourPort(
                    port.Liman_Id,
                    port.Adi?.Trim() ?? string.Empty,
                    port.Sira_No
                ))
                .ToList();
        }
        catch (TourCatalogUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException
                or JsonException
                or NotSupportedException
                or TaskCanceledException
        )
        {
            LogPortRequestFailed(logger, externalTourId, exception);
            throw new TourCatalogUnavailableException(
                "Tur kalkış noktaları şu anda alınamıyor.",
                exception
            );
        }
    }

    public async Task<TourAvailability?> GetAvailabilityAsync(
        int externalTourId,
        int externalDeparturePortId,
        int saleType,
        CancellationToken cancellationToken
    )
    {
        await EnsureSupportedTourAsync(externalTourId, cancellationToken);

        try
        {
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/Data/tur-detay-full?turId={externalTourId}&kalkisLimanId={externalDeparturePortId}&tipi={saleType}"
            );
            var detail = await GetAsync<EasyTicketTourDetail>(
                url,
                cancellationToken
            );
            if (detail?.TurBilgisi is null)
            {
                return null;
            }

            var prices = (detail.Fiyatlar ?? [])
                .Select(price => new TourPrice(
                    price.Fiyat_Id,
                    price.Yolcu_Tipi_Id,
                    price.Yolcu_Tipi?.Trim() ?? string.Empty,
                    price.Tl_Tekyon_Fiyat > 0
                        ? price.Tl_Tekyon_Fiyat
                        : price.Tekyon_Fiyat,
                    string.IsNullOrWhiteSpace(price.Doviz_Tipi)
                        ? "TRY"
                        : price.Doviz_Tipi.Trim().ToUpperInvariant(),
                    price.Kdv
                ))
                .ToList();

            var departures = (detail.Seferler ?? [])
                .Select(trip => new TourDeparture(
                    trip.Id,
                    trip.Sefer_Id,
                    trip.Kalkis_Liman_Id,
                    trip.Kalkis_Liman?.Trim() ?? string.Empty,
                    trip.Sefer_Tarihi.HasValue
                        ? DateOnly.FromDateTime(trip.Sefer_Tarihi.Value)
                        : null,
                    ParseTime(trip.Sefer_Saati)
                ))
                .ToList();

            return new TourAvailability(
                detail.TurBilgisi.Id,
                detail.TurBilgisi.Tur_Adi?.Trim() ?? string.Empty,
                detail.TurBilgisi.Kategori?.Trim() ?? string.Empty,
                detail.TurBilgisi.Satis_Tipi,
                prices,
                departures
            );
        }
        catch (TourCatalogUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException
                or JsonException
                or NotSupportedException
                or TaskCanceledException
        )
        {
            LogAvailabilityRequestFailed(
                logger,
                externalTourId,
                externalDeparturePortId,
                exception
            );
            throw new TourCatalogUnavailableException(
                "Tur seferleri şu anda alınamıyor.",
                exception
            );
        }
    }

    private async Task EnsureSupportedTourAsync(
        int externalTourId,
        CancellationToken cancellationToken
    )
    {
        var catalog = await ListAsync(cancellationToken);
        if (catalog.All(tour => tour.ExternalTourId != externalTourId))
        {
            throw new KeyNotFoundException("Tur bulunamadı.");
        }
    }

    private async Task<T?> GetAsync<T>(
        string relativeUrl,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Add("X-Api-Key", _options.ApiKey);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(
            JsonOptions,
            cancellationToken
        );
    }

    private void EnsureConfigured()
    {
        if (
            string.IsNullOrWhiteSpace(_options.BaseUrl)
            || string.IsNullOrWhiteSpace(_options.ApiKey)
        )
        {
            throw new TourCatalogUnavailableException(
                "EasyTicket bağlantısı yapılandırılmamış."
            );
        }
    }

    private static EasyTicketCategory ResolveCategory(
        IReadOnlyList<EasyTicketCategory> categories,
        CategorySpecification specification
    )
    {
        var match = categories.FirstOrDefault(category =>
        {
            var normalizedName = Normalize(category.Kategori_Adi);
            return specification.Aliases.Any(alias =>
                normalizedName.Contains(Normalize(alias), StringComparison.Ordinal)
            );
        });

        return match ?? new EasyTicketCategory
        {
            Id = specification.FallbackId,
            Kategori_Adi = specification.DisplayName,
        };
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (
                CharUnicodeInfo.GetUnicodeCategory(character)
                    != UnicodeCategory.NonSpacingMark
                && char.IsLetterOrDigit(character)
            )
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var time)
            ? time
            : null;

    private sealed record CategorySpecification(
        string Key,
        string DisplayName,
        int FallbackId,
        string[] Aliases
    );

    private sealed record ResolvedCategory(
        CategorySpecification Specification,
        EasyTicketCategory Category
    );

    private sealed class EasyTicketCategory
    {
        public int Id { get; init; }

        public string? Kategori_Adi { get; init; }
    }

    private sealed class EasyTicketTour
    {
        public int Id { get; init; }

        public string? Tur_Adi { get; init; }
    }

    private sealed class EasyTicketTourPort
    {
        public int Liman_Id { get; init; }

        public string? Adi { get; init; }

        public int Sira_No { get; init; }
    }

    private sealed class EasyTicketTourDetail
    {
        public EasyTicketTourDetailInfo? TurBilgisi { get; init; }

        public List<EasyTicketTourDetailPrice>? Fiyatlar { get; init; }

        public List<EasyTicketTourDetailTrip>? Seferler { get; init; }
    }

    private sealed class EasyTicketTourDetailInfo
    {
        public int Id { get; init; }

        public string? Tur_Adi { get; init; }

        public string? Kategori { get; init; }

        public int Satis_Tipi { get; init; }
    }

    private sealed class EasyTicketTourDetailPrice
    {
        public int Fiyat_Id { get; init; }

        public int Yolcu_Tipi_Id { get; init; }

        public decimal Tekyon_Fiyat { get; init; }

        public decimal Tl_Tekyon_Fiyat { get; init; }

        public string? Doviz_Tipi { get; init; }

        public int Kdv { get; init; }

        public string? Yolcu_Tipi { get; init; }
    }

    private sealed class EasyTicketTourDetailTrip
    {
        public int Id { get; init; }

        public int Sefer_Id { get; init; }

        public int Kalkis_Liman_Id { get; init; }

        public string? Kalkis_Liman { get; init; }

        public string? Sefer_Saati { get; set; }

        public DateTime? Sefer_Tarihi { get; set; }

        public DateTime? Tarih
        {
            set => Sefer_Tarihi = value;
        }

        public string? Saat
        {
            set => Sefer_Saati = value;
        }
    }
}
