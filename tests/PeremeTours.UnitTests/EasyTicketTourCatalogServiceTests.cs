using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Tours;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.UnitTests;

public sealed class EasyTicketTourCatalogServiceTests
{
    [Fact]
    public async Task ListReturnsOnlySupportedCategoriesAndCachesResult()
    {
        var handler = new StubHttpMessageHandler(new Dictionary<string, string>
        {
            ["/api/Data/kategoriler"] =
                """[{"id":1,"kategori_Adi":"Boğaz Turu"},{"id":2,"kategori_Adi":"Türk Gecesi Dinner Cruise"},{"id":4,"kategori_Adi":"Adalar"},{"id":7,"kategori_Adi":"Sunset"},{"id":9,"kategori_Adi":"DayTime"}]""",
            ["/api/Data/turlar/1"] = """[{"id":11,"tur_Adi":"Boğaz Turu"}]""",
            ["/api/Data/turlar/2"] = """[{"id":22,"tur_Adi":"Türk Gecesi"}]""",
            ["/api/Data/turlar/7"] = """[{"id":77,"tur_Adi":"Sunset Cruise"}]""",
            ["/api/Data/turlar/9"] = """[{"id":99,"tur_Adi":"DayTime"}]""",
        });
        var service = CreateService(handler);

        var first = await service.ListAsync(CancellationToken.None);
        var second = await service.ListAsync(CancellationToken.None);

        Assert.Equal(4, first.Count);
        Assert.Equal(
            [
                TourCategoryKeys.Bosphorus,
                TourCategoryKeys.TurkishNight,
                TourCategoryKeys.Sunset,
                TourCategoryKeys.Daytime,
            ],
            first.Select(tour => tour.CategoryKey)
        );
        Assert.Same(first, second);
        Assert.Equal(5, handler.RequestCount);
        Assert.True(handler.AllRequestsHadApiKey);
    }

    [Fact]
    public async Task AvailabilityMapsAlternativeDateAndTimeFields()
    {
        var handler = new StubHttpMessageHandler(new Dictionary<string, string>
        {
            ["/api/Data/kategoriler"] = """[{"id":7,"kategori_Adi":"Sunset"}]""",
            ["/api/Data/turlar/1"] = "[]",
            ["/api/Data/turlar/2"] = "[]",
            ["/api/Data/turlar/7"] = """[{"id":77,"tur_Adi":"Sunset Cruise"}]""",
            ["/api/Data/turlar/9"] = "[]",
            ["/api/Data/tur-limanlar/77"] = """[{"liman_Id":3,"adi":"Kabataş","sira_No":1}]""",
            ["/api/Data/tur-detay-full?turId=77&kalkisLimanId=3&tipi=2"] =
                """{"turBilgisi":{"id":77,"tur_Adi":"Sunset Cruise","kategori":"Sunset","satis_Tipi":2},"fiyatlar":[{"fiyat_Id":8,"yolcu_Tipi_Id":1,"tl_Tekyon_Fiyat":950,"doviz_Tipi":"TL","kdv":20,"yolcu_Tipi":"Yetişkin"}],"seferler":[{"id":4,"sefer_Id":44,"kalkis_Liman_Id":3,"kalkis_Liman":"Kabataş","tarih":"2026-09-12","saat":"18:30"}]}""",
        });
        var service = CreateService(handler);

        var result = await service.GetAvailabilityAsync(
            77,
            3,
            2,
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal(950, result.Prices.Single().Amount);
        Assert.Equal(new DateOnly(2026, 9, 12), result.Departures.Single().Date);
        Assert.Equal(new TimeOnly(18, 30), result.Departures.Single().Time);
    }

    [Fact]
    public async Task ActualUpstreamShapePreservesTicketTypesNotesAndRequestedPort()
    {
        var handler = new StubHttpMessageHandler(new Dictionary<string, string>
        {
            ["/api/Data/kategoriler"] = """[{"id":7,"kategori_Adi":"Sunset"}]""",
            ["/api/Data/turlar/1"] = "[]",
            ["/api/Data/turlar/2"] = "[]",
            ["/api/Data/turlar/7"] = """[{"id":77,"tur_Adi":"Sunset"}]""",
            ["/api/Data/turlar/9"] = "[]",
            ["/api/Data/tur-limanlar/77"] = """[{"liman_Id":3,"adi":"Kabataş","sira_No":1}]""",
            ["/api/Data/tur-detay-full?turId=77&kalkisLimanId=3&tipi=2"] =
                """{"turBilgisi":{"id":77,"tur_Adi":"Sunset","bilet_Notu":"Transfer yok","bilet_Notu_En":"No transfer"},"fiyatlar":[{"fiyat_Id":234,"yolcu_Tipi_Id":8,"tekyon_Fiyat":10,"tl_Tekyon_Fiyat":350,"doviz_Tipi":"EUR","yolcu_Tipi":"İkramsız","yolcu_Tipi_En":"Without refreshments"}],"seferler":[{"id":8707,"tarih":"2026-10-03T00:00:00","saat":"16:30:00","kalkis_Liman_Adi":null}]}""",
        });

        var result = await CreateService(handler).GetAvailabilityAsync(77, 3, 2, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.Departures.Single().ExternalPortId);
        Assert.Equal(8707, result.Departures.Single().ExternalId);
        Assert.Equal("TRY", result.Prices.Single().Currency);
        Assert.Equal(350m, result.Prices.Single().Amount);
        Assert.Equal("Without refreshments", result.Prices.Single().PassengerTypeEn);
        Assert.Equal("No transfer", result.BookingNoteEn);
        Assert.Equal(TourCategoryKeys.Sunset, result.CategoryKey);
    }

    [Fact]
    public async Task AvailabilityReturnsOnlyTheRequestedPortsTripsAndRejectsOtherPorts()
    {
        // The provider sends every port's trips for any kalkisLimanId and names the port without an id.
        const string detail =
            """{"turBilgisi":{"id":77,"tur_Adi":"Sunset","satis_Tipi":1},"fiyatlar":[],"seferler":[{"id":9863,"tarih":"2026-11-17T00:00:00","saat":"16:30:00","kalkis_Liman_Adi":"Kabataş"},{"id":9908,"tarih":"2026-11-17T00:00:00","saat":"16:40:00","kalkis_Liman_Adi":"Beşiktaş"}]}""";
        var handler = new StubHttpMessageHandler(new Dictionary<string, string>
        {
            ["/api/Data/kategoriler"] = """[{"id":7,"kategori_Adi":"Sunset"}]""",
            ["/api/Data/turlar/1"] = "[]",
            ["/api/Data/turlar/2"] = "[]",
            ["/api/Data/turlar/7"] = """[{"id":77,"tur_Adi":"Sunset"}]""",
            ["/api/Data/turlar/9"] = "[]",
            ["/api/Data/tur-limanlar/77"] = """[{"liman_Id":3,"adi":"Kabataş","sira_No":1},{"liman_Id":2,"adi":"Beşiktaş","sira_No":2}]""",
            ["/api/Data/tur-detay-full?turId=77&kalkisLimanId=3&tipi=2"] = detail,
            ["/api/Data/tur-detay-full?turId=77&kalkisLimanId=2&tipi=2"] = detail,
        });
        var service = CreateService(handler);

        var kabatas = await service.GetAvailabilityAsync(77, 3, 2, CancellationToken.None);
        var besiktas = await service.GetAvailabilityAsync(77, 2, 2, CancellationToken.None);
        var unknown = await service.GetAvailabilityAsync(77, 999, 2, CancellationToken.None);

        var kabatasTrip = Assert.Single(kabatas!.Departures);
        Assert.Equal((9863, 3, "Kabataş", new TimeOnly(16, 30)), (kabatasTrip.ExternalId, kabatasTrip.ExternalPortId, kabatasTrip.PortName, kabatasTrip.Time!.Value));
        var besiktasTrip = Assert.Single(besiktas!.Departures);
        Assert.Equal((9908, 2, "Beşiktaş", new TimeOnly(16, 40)), (besiktasTrip.ExternalId, besiktasTrip.ExternalPortId, besiktasTrip.PortName, besiktasTrip.Time!.Value));
        Assert.Equal(9908, besiktasTrip.ExternalTripId);
        Assert.Null(unknown);
    }

    private static EasyTicketTourCatalogService CreateService(
        StubHttpMessageHandler handler
    )
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        var options = Options.Create(new EasyTicketOptions
        {
            BaseUrl = "https://example.test",
            ApiKey = "test-api-key",
            CacheMinutes = 5,
        });
        return new EasyTicketTourCatalogService(
            httpClient,
            options,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<EasyTicketTourCatalogService>.Instance
        );
    }

    private sealed class StubHttpMessageHandler(
        IReadOnlyDictionary<string, string> responses
    ) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public bool AllRequestsHadApiKey { get; private set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            AllRequestsHadApiKey &= request.Headers.TryGetValues(
                "X-Api-Key",
                out var values
            ) && values.Single() == "test-api-key";

            var path = request.RequestUri!.PathAndQuery;
            var response = responses.TryGetValue(path, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.Content = new StringContent(
                json ?? "{}",
                Encoding.UTF8,
                "application/json"
            );
            return Task.FromResult(response);
        }
    }
}
