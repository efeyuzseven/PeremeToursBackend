using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.Infrastructure.Tickets;

internal sealed class EasyTicketCancellationGateway(HttpClient http, IOptions<EasyTicketOptions> options) : IEasyTicketCancellationGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task CancelAsync(TourTicket ticket, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || http.BaseAddress?.Scheme != Uri.UriSchemeHttps
            || !Guid.TryParse(ticket.ExternalVoucherGuid, out var voucher))
            throw new CancellationGatewayException("PROVIDER_CANCELLATION_CHECK_FAILED");
        // Check the genuine voucher contents before cancelling. No email/PNR-based ownership guesses.
        await CheckVoucherAsync(ticket, voucher, settings.ApiKey, cancellationToken);
        try
        {
            // Provider Swagger requires a JSON STRING, not { voucherGuid: ... }.
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/data/web-bilet-iptal")
            { Content = JsonContent.Create(voucher.ToString("D"), options: JsonOptions) };
            request.Headers.Add("X-Api-Key", settings.ApiKey);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNKNOWN");
            var result = await response.Content.ReadFromJsonAsync<CancelResponse>(JsonOptions, cancellationToken);
            if (result is null) throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNKNOWN");
            if (result.Success != true && result.Result != 1)
                throw new CancellationGatewayException(result.Success.HasValue || result.Result.HasValue
                    ? "PROVIDER_CANCELLATION_REJECTED" : "PROVIDER_CANCELLATION_UNKNOWN");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        { throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNKNOWN"); }
    }

    private async Task CheckVoucherAsync(TourTicket ticket, Guid voucher, string apiKey, CancellationToken cancellationToken)
    {
        // Only this read-only GET is retried. Never retry either provider cancellation or bank reversal.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/data/ebilet-getir?guid={voucher:D}");
                request.Headers.Add("X-Api-Key", apiKey);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd("PeremeTours/1.0");
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    if (attempt == 2) throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNAVAILABLE");
                }
                else
                {
                    if (!response.IsSuccessStatusCode) throw new CancellationGatewayException("PROVIDER_CANCELLATION_CHECK_FAILED");
                    var content = await response.Content.ReadFromJsonAsync<VoucherResponse>(JsonOptions, timeout.Token);
                    if (content?.Success != true || content.Biletler is null || content.Biletler.Count != ticket.Passengers.Count
                        || content.Biletler.DistinctBy(item => item.Guid).Count() != content.Biletler.Count
                        || content.Biletler.Any(item => !ticket.Passengers.Any(passenger =>
                            string.Equals(passenger.ExternalTicketGuid, item.Guid, StringComparison.OrdinalIgnoreCase) && passenger.Pnr == item.Pnr))
                        || content.Biletler.Sum(item => item.ToplamTutar) != ticket.Amount)
                        throw new CancellationGatewayException("PROVIDER_CANCELLATION_CHECK_FAILED");
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                if (attempt == 2 || cancellationToken.IsCancellationRequested)
                    throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNAVAILABLE");
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            { throw new CancellationGatewayException("PROVIDER_CANCELLATION_CHECK_FAILED"); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken); }
            catch (OperationCanceledException) { throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNAVAILABLE"); }
        }
        throw new CancellationGatewayException("PROVIDER_CANCELLATION_UNAVAILABLE");
    }

    private sealed record VoucherResponse(bool Success, List<VoucherTicket>? Biletler);
    private sealed record VoucherTicket(string? Guid, string? Pnr, decimal ToplamTutar);
    private sealed record CancelResponse(bool? Success, int? Result);
}
