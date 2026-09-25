using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PeremeTours.IntegrationTests;

public sealed class HealthEndpointTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "ConnectionStrings:PeremeToursDatabase",
                "Host=localhost;Port=5432;Database=peremetours_tests;Username=postgres;Password=postgres"
            );
            builder.UseSetting(
                "Jwt:Key",
                "peremetours-integration-tests-signing-key-2026-secure"
            );
        }).CreateClient();
    }

    [Fact]
    public async Task HealthReturnsOk()
    {
        var response = await _client.GetAsync("/api/v1/system/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PaymentInitializationStaysUnavailableUntilPosIsEnabled()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/payments/tour/initialize",
            new
            {
                externalTourId = 1,
                externalDeparturePortId = 1,
                externalDepartureId = 1,
                externalTripId = 1,
                externalPriceId = 1,
                tourDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
                guestCount = 1,
                customerName = "Test User",
                customerEmail = "test@example.com",
                language = "tr",
                card = new
                {
                    holderName = "Test User",
                    number = "4111111111111111",
                    securityCode = "123",
                    expiryMonth = 12,
                    expiryYear = DateTime.UtcNow.Year + 1,
                },
            }
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
