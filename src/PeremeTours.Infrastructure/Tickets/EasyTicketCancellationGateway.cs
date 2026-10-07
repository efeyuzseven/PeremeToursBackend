using System.Net.Http.Json;
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
        try
        {
            using var checkRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/data/ebilet-getir?guid={voucher:D}");
            checkRequest.Headers.Add("X-Api-Key", settings.ApiKey);
            using var checkResponse = await http.SendAsync(checkRequest, cancellationToken);
            if (!checkResponse.IsSuccessStatusCode) throw new CancellationGatewayException("PROVIDER_CANCELLATION_CHECK_FAILED");
            var content = await checkResponse.Content.ReadFromJsonAsync<VoucherResponse>(JsonOptions, cancellationToken);
            if (content?.Success != true || content.Biletler is null || content.Biletler.Count != ticket.Passengers.Count
                || content.Biletler.DistinctBy(item => item.Guid).Count() != content.Biletler.Count
                || content.Biletler.Any(item => !ticket.Passengers.Any(passenger =>
                    string.Equals(passenger.ExternalTicketGuid, item.Guid, StringComparison.OrdinalIgnoreCase) && passenger.Pnr == item.Pnr))
                || content.Biletler.Sum(item => item.ToplamTutar) != ticket.Amount)
                throw new CancellationGatewayException("PROVIDER_CANCELLATION_CHECK_FAILED");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        { throw new CancellationGatewayException("PROVIDER_CANCELLATION_CHECK_FAILED"); }
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

    private sealed record VoucherResponse(bool Success, List<VoucherTicket>? Biletler);
    private sealed record VoucherTicket(string? Guid, string? Pnr, decimal ToplamTutar);
    private sealed record CancelResponse(bool? Success, int? Result);
}
