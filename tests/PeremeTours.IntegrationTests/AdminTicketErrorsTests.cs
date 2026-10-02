using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.IntegrationTests;

public sealed class AdminTicketErrorsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public AdminTicketErrorsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-admin-policy-test-signing-key-2026-secure");
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<ITicketErrorService, StubErrors>();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        }).CreateClient();
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task ErrorRecordsAreAdminOnly(string? role, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/ticket-errors?page=1&pageSize=20");
        if (role is not null) request.Headers.Add("X-Test-Role", role);
        using var response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("stage=999")]
    [InlineData("page=0")]
    [InlineData("pageSize=101")]
    public async Task InvalidFiltersAreRejected(string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/ticket-errors?{query}");
        request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class StubErrors : ITicketErrorService
    {
        public Task<TicketErrorPage> ListAsync(string? search, TicketErrorStage? stage, int page, int pageSize, CancellationToken cancellationToken)
            => Task.FromResult(new TicketErrorPage([], 0, page, pageSize));
    }
    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}
