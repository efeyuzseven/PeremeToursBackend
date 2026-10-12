using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PeremeTours.Application.Payments;
using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.IntegrationTests;

public sealed class PaymentDomainCutoverTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestOrigin = "https://d2bmjk2h6qp4lz.cloudfront.net";
    private readonly WebApplicationFactory<Program> _app;
    private readonly HttpClient _client;

    public PaymentDomainCutoverTests(WebApplicationFactory<Program> factory)
    {
        _app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-domain-cutover-test-signing-key-2026-secure");
            builder.UseSetting("Payments:Ziraat:FrontendOrigin", TestOrigin);
            builder.ConfigureTestServices(services => services.AddScoped<ITourPaymentService, FakePayments>());
        });
        _client = _app.CreateClient();
    }

    [Theory]
    [InlineData("https://pereme.com.tr")]
    [InlineData("https://www.pereme.com.tr")]
    [InlineData(TestOrigin)]
    public async Task ProductionAndLegacyOriginsSupportPreflightWithoutStartingPayments(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/payments/tour/initialize");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task UntrustedOriginCannotReadTheApi()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/payments/tour/initialize");
        request.Headers.Add("Origin", "https://pereme.com.tr.evil.test");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        using var response = await _client.SendAsync(request);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task BankFrameAllowsBothProductionHostsAndTheExistingTestHost()
    {
        var store = _app.Services.GetRequiredService<ThreeDSecureFrameStore>();
        var token = store.Publish("<!doctype html><p>Mock bank frame; no card data.</p>");
        using var response = await _client.GetAsync($"/api/v1/payments/tour/3d/{token:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal($"frame-ancestors {TestOrigin} https://pereme.com.tr https://www.pereme.com.tr", csp);
        Assert.DoesNotContain('*', csp);
        using var consumed = await _client.GetAsync($"/api/v1/payments/tour/3d/{token:D}");
        Assert.Equal(HttpStatusCode.Gone, consumed.StatusCode);
    }

    [Fact]
    public async Task CallbackMessagesTargetOnlyConfiguredOriginsNotRequestHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/ziraat/callback")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["mock"] = "only" }) };
        request.Headers.Add("Origin", "https://attacker.example.test");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"[\"{TestOrigin}\",\"https://pereme.com.tr\",\"https://www.pereme.com.tr\"]", html, StringComparison.Ordinal);
        Assert.Contains("window.parent.postMessage", html, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example.test", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"*\"", html, StringComparison.Ordinal);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains($"frame-ancestors {TestOrigin} https://pereme.com.tr https://www.pereme.com.tr", csp, StringComparison.Ordinal);
        Assert.DoesNotContain('*', csp);
        Assert.Contains("nonce-", csp, StringComparison.Ordinal);
    }

    private sealed class FakePayments : ITourPaymentService
    {
        public PaymentAvailability GetAvailability() => new(false, "Mock");
        public Task<TourPaymentStatus?> GetStatusAsync(Guid attemptId, CancellationToken cancellationToken) => Task.FromResult<TourPaymentStatus?>(null);
        public Task<StartTourPaymentResult> StartAsync(StartTourPaymentCommand command, CancellationToken cancellationToken)
            => throw new InvalidOperationException("These tests must not start a payment.");
        public Task<CompleteTourPaymentResult> CompleteAsync(IReadOnlyDictionary<string, string> formFields, CancellationToken cancellationToken)
            => Task.FromResult(new CompleteTourPaymentResult(false, "PRM-MOCK-NO-TRANSACTION", "Mock callback; no bank operation."));
    }
}
