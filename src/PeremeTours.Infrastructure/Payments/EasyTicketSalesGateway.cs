using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeremeTours.Domain.Tickets;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.Infrastructure.Payments;

internal sealed record EasyTicketSaleResult(bool IsComplete, string? VoucherGuid, IReadOnlyList<EasyTicketIssuedTicket> Tickets);
internal sealed record EasyTicketIssuedTicket(string? Guid, string? Pnr);

internal interface IEasyTicketSalesGateway
{
    Task<EasyTicketSaleResult> IssueAsync(TourTicket ticket, CancellationToken cancellationToken);
}

internal sealed class EasyTicketSalesGateway(HttpClient httpClient, IOptions<EasyTicketOptions> options) : IEasyTicketSalesGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EasyTicketOptions _options = options.Value;

    // Verified against AcenteSatisIstekDto / AcenteBiletDetayDto in the provider's Swagger.
    // This is a SALE, not a capacity hold. Do not retry an ambiguous response: PO must be reconciled first.
    public async Task<EasyTicketSaleResult> IssueAsync(TourTicket ticket, CancellationToken cancellationToken)
    {
        if (ticket.PaymentStatus != TicketPaymentStatus.Paid || ticket.ExternalTripId is not > 0
            || ticket.Passengers.Count != ticket.GuestCount || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new PaymentTicketingException();
        }
        var payload = new
        {
            eposta = ticket.CustomerEmail,
            telefon = ticket.CustomerPhone,
            po = ticket.TicketCode,
            biletler = ticket.Passengers.OrderBy(item => item.Sequence).Select(item => new
            {
                seferId = ticket.ExternalTripId.Value,
                fiyatId = item.ExternalPriceId,
                dovizKodu = "TL",
                biletTutari = item.UnitAmount,
                yolcuAdi = item.FirstName,
                yolcuSoyadi = item.LastName,
                yolcuCinsiyet = item.Gender == "male" ? "E" : "K",
                yolcuDogumTarihi = item.BirthDate.ToDateTime(TimeOnly.MinValue),
                yolcuUyruk = item.Nationality == "TR" ? "TC" : "XXX",
                yolcuPasaportNo = item.IdentityNumber,
            }),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/data/web-bilet-satis")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        request.Headers.Add("X-Api-Key", _options.ApiKey);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 131_072)
                throw new PaymentTicketingException();
            var data = await response.Content.ReadFromJsonAsync<SaleResponse>(JsonOptions, cancellationToken)
                ?? throw new PaymentTicketingException();
            var tickets = data.Biletler ?? [];
            var complete = data.Success && !string.IsNullOrWhiteSpace(data.VoucherGuid)
                && data.VoucherGuid.Length <= 128 && tickets.Count == ticket.GuestCount
                && tickets.All(item => !string.IsNullOrWhiteSpace(item.Guid) && item.Guid.Length <= 128
                    && !string.IsNullOrWhiteSpace(item.Pnr) && item.Pnr.Length <= 128)
                && tickets.DistinctBy(item => item.Guid).Count() == tickets.Count
                && (string.IsNullOrWhiteSpace(data.Po) || data.Po == ticket.TicketCode);
            return new EasyTicketSaleResult(complete, data.VoucherGuid, tickets);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        {
            // Never include the provider body, passenger details, API key or raw exception in logs/responses.
            throw new PaymentTicketingException();
        }
    }

    private sealed record SaleResponse(bool Success, string? VoucherGuid, string? Po, List<EasyTicketIssuedTicket>? Biletler);
}

internal sealed class PaymentTicketingException() : Exception("Bilet sonucu kontrol edilmeli.");
