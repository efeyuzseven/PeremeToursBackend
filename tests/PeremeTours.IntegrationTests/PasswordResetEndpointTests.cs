using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PeremeTours.Application.Authentication;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.IntegrationTests;

public sealed class PasswordResetEndpointTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Requests _requests = new();
    public PasswordResetEndpointTests()
    {
        _connection.Open();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-password-reset-fixture-signing-key-2026");
            builder.UseSetting("MailSettings:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<PeremeToursDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<PeremeToursDbContext>>();
                services.AddDbContext<PeremeToursDbContext>(options => options.UseSqlite(_connection).ReplaceService<IModelCustomizer, TestModel>());
                services.RemoveAll<IPasswordResetRequests>(); services.AddSingleton<IPasswordResetRequests>(_requests);
            });
        });
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task ForgotReturnsTheSameUncachedResponseForEveryValidEmail()
    {
        string? first = null;
        foreach (var email in new[] { "active@example.test", "inactive@example.test", "unknown@example.test" })
        {
            using var response = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email, language = "tr" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            var body = await response.Content.ReadAsStringAsync(); first ??= body; Assert.Equal(first, body);
            Assert.DoesNotContain(email, body, StringComparison.Ordinal);
        }
        Assert.Equal(3, _requests.Items.Count);
    }

    [Fact]
    public async Task EnglishRequestsAndUntrustedOriginsDoNotControlResetLinks()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new { email = "fixture@example.test", language = "en", redirectUrl = "https://attacker.example.test" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("active account", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("en", Assert.Single(_requests.Items).Language);
    }

    [Fact]
    public async Task RequestRateLimitStopsAbuseWithoutSendingEmail()
    {
        for (var index = 0; index < 5; index++)
        {
            using var response = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "fixture@example.test" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        using var rejected = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "fixture@example.test" });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode); Assert.Equal(5, _requests.Items.Count);
    }

    [Theory]
    [InlineData("bad-email", "tr")]
    [InlineData("valid@example.test", "fr")]
    public async Task InvalidRequestsAreNotQueued(string email, string language)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email, language });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty(_requests.Items);
    }

    [Theory]
    [InlineData(UserRoles.Admin)]
    [InlineData(UserRoles.User)]
    public async Task RealJwtSessionsAreRevokedAfterResetAndRoleIsPreserved(string role)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>();
            var user = new UserAccount { Id = Guid.NewGuid(), Email = "fixture@example.test", NormalizedEmail = "FIXTURE@EXAMPLE.TEST",
                FirstName = "Fixture", PasswordHash = string.Empty, Role = role, IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
            user.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(user, "OriginalPassword1");
            db.Users.Add(user);
            db.PasswordResetTokens.Add(new() { Id = Guid.NewGuid(), UserId = user.Id, User = user,
                TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30) });
            db.SaveChanges();
        }
        var oldAccessToken = await Login("OriginalPassword1");
        Assert.Equal(HttpStatusCode.OK, await Me(oldAccessToken));
        using var reset = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "UpdatedPassword2" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode); Assert.True(reset.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(oldAccessToken));
        using var oldLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "fixture@example.test", password = "OriginalPassword1" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var newAccessToken = await Login("UpdatedPassword2"); Assert.Equal(HttpStatusCode.OK, await Me(newAccessToken));
        using var replay = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "ChangedAgain3" });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using var finalScope = _factory.Services.CreateScope();
        Assert.Equal(role, finalScope.ServiceProvider.GetRequiredService<PeremeToursDbContext>().Users.Single().Role);
    }

    [Fact]
    public async Task UnknownAndWeakTokensDoNotExposeTokenValues()
    {
        var token = new string('A', 64);
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "ValidPassword123" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<string> Login(string password)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "fixture@example.test", password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }
    private async Task<HttpStatusCode> Me(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request); return response.StatusCode;
    }
    private sealed class Requests : IPasswordResetRequests
    {
        public List<PasswordResetRequest> Items { get; } = [];
        public bool TryEnqueue(PasswordResetRequest request) { Items.Add(request); return true; }
    }
    public sealed class TestModel(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
        }
    }
    public void Dispose() { _client.Dispose(); _factory.Dispose(); _connection.Dispose(); }
}
