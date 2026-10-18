using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;

namespace PeremeTours.Infrastructure.Payments;

public interface IRecaptchaVerifier
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken);
}

public sealed class RecaptchaVerifier(
    HttpClient httpClient,
    IOptions<PaymentProtectionOptions> options,
    ILogger<RecaptchaVerifier> logger) : IRecaptchaVerifier
{
    private static readonly Uri VerifyUri = new("https://www.google.com/recaptcha/api/siteverify");
    private static readonly Action<ILogger, string, Exception?> LogRejected =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4101, nameof(LogRejected)),
            "reCAPTCHA verification rejected. ErrorCodes={ErrorCodes}");
    private static readonly Action<ILogger, Exception?> LogUnavailable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(4102, nameof(LogUnavailable)),
            "reCAPTCHA verification request failed.");

    private readonly PaymentProtectionOptions _options = options.Value;

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken)
    {
        if (!_options.RecaptchaEnabled) return true;
        if (string.IsNullOrWhiteSpace(_options.RecaptchaSecretKey))
            throw new PaymentConfigurationException("Güvenlik doğrulaması yapılandırılmamış.");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;

        var fields = new Dictionary<string, string> { ["secret"] = _options.RecaptchaSecretKey, ["response"] = token };
        if (!string.IsNullOrWhiteSpace(remoteIp)) fields["remoteip"] = remoteIp;

        try
        {
            using var response = await httpClient.PostAsync(VerifyUri, new FormUrlEncodedContent(fields), cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
            if (result?.Success == true) return true;
            LogRejected(logger, string.Join(",", result?.ErrorCodes ?? []), null);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LogUnavailable(logger, exception);
            return false;
        }
    }

    private sealed class SiteVerifyResponse
    {
        [JsonPropertyName("success")] public bool Success { get; init; }
        [JsonPropertyName("error-codes")] public string[]? ErrorCodes { get; init; }
    }
}
