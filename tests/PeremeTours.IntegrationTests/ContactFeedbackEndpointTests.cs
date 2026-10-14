using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;

namespace PeremeTours.IntegrationTests;

public sealed class ContactFeedbackEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly Service _service = new();
    private static readonly Guid ContactId = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();
    public ContactFeedbackEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-feedback-endpoint-test-signing-key-secure");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IContactFeedbackService>(_service);
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            });
        }).CreateClient();
    }
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)][InlineData("User", HttpStatusCode.Forbidden)][InlineData("Admin", HttpStatusCode.OK)]
    public async Task AllFeedbackReadsAndReplyWritesAreAdministratorOnly(string? role, HttpStatusCode expected)
    {
        foreach (var path in new[] { "/api/v1/admin/feedback", $"/api/v1/admin/feedback/{ContactId}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path); if (role is not null) request.Headers.Add("X-Test-Role", role);
            using var response = await _client.SendAsync(request); Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.OK) Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        }
        using var send = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/feedback/{ContactId}/replies") { Content = JsonContent.Create(new ContactReplySubmission(Guid.NewGuid(), 0, "Fixture response")) };
        if (role is not null) send.Headers.Add("X-Test-Role", role);
        using var sent = await _client.SendAsync(send); Assert.Equal(expected == HttpStatusCode.OK ? HttpStatusCode.Accepted : expected, sent.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, _service.Replies);
    }
    [Theory]
    [InlineData("page=0")][InlineData("pageSize=101")][InlineData("filter=Invalid")]
    public async Task InvalidListArgumentsFailValidation(string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/admin/feedback?{query}"); request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Theory]
    [InlineData("id")][InlineData("blank")][InlineData("long")][InlineData("revision")][InlineData("nul")]
    public async Task InvalidReplyNeverCallsTheReplyService(string mode)
    {
        var body = new ContactReplySubmission(Guid.NewGuid(), 0, "Fixture");
        body = mode switch { "id" => body with { ReplyId = Guid.Empty }, "blank" => body with { Body = " " }, "long" => body with { Body = new string('x', 6001) },
            "revision" => body with { ExpectedRevision = -1 }, _ => body with { Body = "Bad\0body" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/feedback/{ContactId}/replies") { Content = JsonContent.Create(body) }; request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, _service.Replies);
    }
    [Theory]
    [InlineData("conflict", HttpStatusCode.Conflict)][InlineData("blocked", HttpStatusCode.Conflict)][InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("disabled", HttpStatusCode.ServiceUnavailable)][InlineData("missing", HttpStatusCode.NotFound)]
    public async Task ReplyFailuresHaveExplicitNonSuccessResults(string mode, HttpStatusCode expected)
    {
        _service.Mode = mode;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/feedback/{ContactId}/replies") { Content = JsonContent.Create(new ContactReplySubmission(Guid.NewGuid(), 0, "Fixture")) }; request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request); Assert.Equal(expected, response.StatusCode);
    }
    private sealed class Service : IContactFeedbackService
    {
        public int Replies { get; private set; }
        public string? Mode { get; set; }
        public Task<ContactFeedbackPage> ListAsync(string? search, ContactFeedbackFilter? filter, int page, int pageSize, CancellationToken cancellationToken)
            => Task.FromResult(new ContactFeedbackPage([], 0, page, pageSize, 0, 0, 0, true));
        public Task<ContactFeedbackDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult<ContactFeedbackDetail?>(new(id, "Fixture", "fixture@example.test", "Subject", "Message", "tr", DateTime.UtcNow, 0, [], []));
        public Task<ContactReplyReceipt?> ReplyAsync(Guid id, Guid adminId, ContactReplySubmission reply, CancellationToken cancellationToken)
        {
            Assert.Equal(AdminId, adminId); Replies++;
            return Mode switch { "conflict" => throw new ContactFeedbackConflictException(), "blocked" => throw new ContactReplyBlockedException(), "forbidden" => throw new ContactFeedbackForbiddenException(),
                "disabled" => throw new ContactMailUnavailableException(), "missing" => Task.FromResult<ContactReplyReceipt?>(null),
                _ => Task.FromResult<ContactReplyReceipt?>(new(reply.ReplyId, id, 1, ContactEmailStatus.Queued)) };
        }
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, AdminId.ToString())], "Test")), "Test")));
        }
    }
}
