using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;

namespace PeremeTours.Infrastructure.Content;

public sealed class GoogleReviewOptions
{
    public const string SectionName = "GoogleReviews";

    public string ApiKey { get; init; } = string.Empty;

    // Google Maps Place ID of the business listing.
    public string PlaceId { get; init; } = string.Empty;

    // Each language is one Place Details call per period; 120 minutes keeps both languages
    // around 720 calls a month, inside Google's free monthly allowance.
    public int CacheMinutes { get; init; } = 120;
}

public sealed class GoogleReviewService(
    HttpClient httpClient,
    IOptions<GoogleReviewOptions> options,
    IMemoryCache cache,
    ILogger<GoogleReviewService> logger) : IGoogleReviewService
{
    private const string FieldMask = "rating,userRatingCount,googleMapsUri,reviews";
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(10);
    // Concurrent visitors on an empty cache must not each call Google; the daily quota is small.
    private static readonly SemaphoreSlim FetchGate = new(1, 1);
    private static readonly Action<ILogger, int, Exception?> LogRejected =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(5101, nameof(LogRejected)),
            "Google Places rejected the review request. Status={Status}");
    private static readonly Action<ILogger, Exception?> LogUnavailable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(5102, nameof(LogUnavailable)),
            "Google Places review request failed.");

    private readonly GoogleReviewOptions _options = options.Value;

    public async Task<GoogleReviewSummary?> GetAsync(string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.PlaceId)) return null;
        language = language == "en" ? "en" : "tr";
        var cacheKey = $"google-reviews:v1:{language}";
        if (cache.TryGetValue(cacheKey, out GoogleReviewSummary? cached)) return cached;

        await FetchGate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached)) return cached;
            var summary = await FetchAsync(language, cancellationToken);
            cache.Set(cacheKey, summary, summary is null
                ? FailureCacheDuration
                : TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 30, 1440)));
            return summary;
        }
        finally
        {
            FetchGate.Release();
        }
    }

    private async Task<GoogleReviewSummary?> FetchAsync(string language, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(_options.PlaceId.Trim())}?languageCode={language}");
        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add("X-Goog-FieldMask", FieldMask);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogRejected(logger, (int)response.StatusCode, null);
                return null;
            }
            var place = await response.Content.ReadFromJsonAsync<PlaceResponse>(cancellationToken);
            return place is null ? null : ToSummary(place);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LogUnavailable(logger, exception);
            return null;
        }
    }

    private static GoogleReviewSummary? ToSummary(PlaceResponse place)
    {
        var mapsUrl = HttpsUrl(place.GoogleMapsUri);
        if (place.Rating is not { } rating || place.UserRatingCount is not > 0 || mapsUrl is null) return null;
        var reviews = (place.Reviews ?? [])
            .Where(review => !string.IsNullOrWhiteSpace(review.Text?.Text)
                && !string.IsNullOrWhiteSpace(review.AuthorAttribution?.DisplayName)
                && review.Rating is >= 1 and <= 5)
            .Select(review => new GoogleReview(
                review.AuthorAttribution!.DisplayName!.Trim(),
                HttpsUrl(review.AuthorAttribution.Uri),
                HttpsUrl(review.AuthorAttribution.PhotoUri),
                review.Rating,
                Truncate(review.Text!.Text!.Trim(), 2000),
                review.Text.LanguageCode is { } shown && review.OriginalText?.LanguageCode is { } original
                    && !string.Equals(shown, original, StringComparison.OrdinalIgnoreCase),
                review.RelativePublishTimeDescription,
                review.PublishTime))
            .ToList();
        return new GoogleReviewSummary(Math.Round(rating, 1), place.UserRatingCount.Value, mapsUrl, reviews);
    }

    private static string? HttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : $"{value[..length].TrimEnd()}…";

    private sealed class PlaceResponse
    {
        [JsonPropertyName("rating")] public decimal? Rating { get; init; }
        [JsonPropertyName("userRatingCount")] public int? UserRatingCount { get; init; }
        [JsonPropertyName("googleMapsUri")] public string? GoogleMapsUri { get; init; }
        [JsonPropertyName("reviews")] public PlaceReview[]? Reviews { get; init; }
    }

    private sealed class PlaceReview
    {
        [JsonPropertyName("rating")] public int Rating { get; init; }
        [JsonPropertyName("text")] public LocalizedText? Text { get; init; }
        [JsonPropertyName("originalText")] public LocalizedText? OriginalText { get; init; }
        [JsonPropertyName("relativePublishTimeDescription")] public string? RelativePublishTimeDescription { get; init; }
        [JsonPropertyName("publishTime")] public DateTimeOffset? PublishTime { get; init; }
        [JsonPropertyName("authorAttribution")] public AuthorAttribution? AuthorAttribution { get; init; }
    }

    private sealed class LocalizedText
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("languageCode")] public string? LanguageCode { get; init; }
    }

    private sealed class AuthorAttribution
    {
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("uri")] public string? Uri { get; init; }
        [JsonPropertyName("photoUri")] public string? PhotoUri { get; init; }
    }
}
