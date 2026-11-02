using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeremeTours.Infrastructure.Content;

namespace PeremeTours.UnitTests;

public sealed class GoogleReviewServiceTests
{
    private const string Place = """
        {
          "rating": 4.66,
          "userRatingCount": 812,
          "googleMapsUri": "https://maps.google.com/?cid=1",
          "reviews": [
            {
              "rating": 5,
              "text": { "text": "Harika bir akşamdı.", "languageCode": "tr" },
              "originalText": { "text": "Harika bir akşamdı.", "languageCode": "tr" },
              "relativePublishTimeDescription": "2 hafta önce",
              "publishTime": "2026-10-19T18:00:00Z",
              "authorAttribution": { "displayName": "Ayşe K.", "uri": "https://www.google.com/maps/contrib/1", "photoUri": "https://lh3.googleusercontent.com/a/1" }
            },
            {
              "rating": 4,
              "text": { "text": "Çok güzel bir tekne.", "languageCode": "tr" },
              "originalText": { "text": "Lovely boat.", "languageCode": "en" },
              "authorAttribution": { "displayName": "John", "uri": "javascript:alert(1)" }
            },
            { "rating": 5, "authorAttribution": { "displayName": "Yorumsuz" } }
          ]
        }
        """;

    [Fact]
    public async Task ReturnsRatingAndReviewsWithSafeLinksAndCachesPerLanguage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Place);
        var service = Create(handler);

        var summary = await service.GetAsync("tr", CancellationToken.None);
        await service.GetAsync("tr", CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(4.7m, summary.Rating);
        Assert.Equal(812, summary.TotalCount);
        Assert.Equal(2, summary.Reviews.Count);
        Assert.False(summary.Reviews[0].Translated);
        Assert.Equal("https://lh3.googleusercontent.com/a/1", summary.Reviews[0].AuthorPhotoUrl);
        Assert.True(summary.Reviews[1].Translated);
        Assert.Null(summary.Reviews[1].AuthorUrl);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("/v1/places/place-1?languageCode=tr", handler.LastPath);
        Assert.Equal("test-key", handler.LastApiKey);

        await service.GetAsync("en", CancellationToken.None);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task MissingSettingsOrAGoogleErrorHideTheSectionWithoutRetryingEveryRequest()
    {
        var unconfigured = new StubHandler(HttpStatusCode.OK, Place);
        Assert.Null(await Create(unconfigured, apiKey: "").GetAsync("tr", CancellationToken.None));
        Assert.Equal(0, unconfigured.Calls);

        var failing = new StubHandler(HttpStatusCode.Forbidden, "{}");
        var service = Create(failing);
        Assert.Null(await service.GetAsync("tr", CancellationToken.None));
        Assert.Null(await service.GetAsync("tr", CancellationToken.None));
        Assert.Equal(1, failing.Calls);
    }

    private static GoogleReviewService Create(StubHandler handler, string apiKey = "test-key") =>
        new(new HttpClient(handler),
            Options.Create(new GoogleReviewOptions { ApiKey = apiKey, PlaceId = "place-1" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<GoogleReviewService>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = request.RequestUri!.PathAndQuery;
            LastApiKey = request.Headers.GetValues("X-Goog-Api-Key").Single();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
