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
                tourDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
                tickets = new[] { new { externalPriceId = 1, quantity = 1 } },
                passengers = new[] { new { externalPriceId = 1, firstName = "Test", lastName = "User", gender = "male",
                    nationality = "TR", identityNumber = "12345678901", birthDate = "1990-01-01" } },
                expectedAmount = 100,
                attemptId = Guid.NewGuid(),
                privacyNoticeAccepted = true,
                customerName = "Test User",
                customerEmail = "test@example.com",
                customerPhone = "+905551234567",
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

    [Fact]
    public async Task MissingTicketSelectionReturnsBadRequestWithoutCallingUpstream()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tours/quote", new
        {
            externalTourId = 2,
            externalDeparturePortId = 3,
            externalDepartureId = 8366,
            tourDate = "2026-10-03",
            tickets = new object?[] { null },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
