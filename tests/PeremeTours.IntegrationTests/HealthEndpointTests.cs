using System.Net;
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
}
