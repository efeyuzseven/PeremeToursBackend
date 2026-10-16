using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PeremeTours.Application.Tickets;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.IntegrationTests;

public sealed class PanelRoleAuthorizationTests : IDisposable
{
    private const string Password = "FixturePassword1";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Cancellation _cancellation = new();
    private readonly Dictionary<string, Guid> _ids = [];

    public PanelRoleAuthorizationTests()
    {
        _connection.Open();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-panel-roles-fixture-signing-key-2026-security");
            builder.UseSetting("MailSettings:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<PeremeToursDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<PeremeToursDbContext>>();
                services.AddDbContext<PeremeToursDbContext>(options => options.UseSqlite(_connection)
                    .ReplaceService<IModelCustomizer, PasswordResetEndpointTests.TestModel>());
                services.AddSingleton<ITourCatalogService, Catalog>();
                // No test can issue a real bank/provider cancellation.
                services.AddSingleton<ITicketCancellationService>(_cancellation);
            });
        });
        _client = _factory.CreateClient();
        WithDb(db =>
        {
            db.Database.EnsureCreated();
            foreach (var role in new[] { UserRoles.Admin, UserRoles.User, UserRoles.Editor, UserRoles.Advisor, UserRoles.Service })
            {
                var email = role.ToLowerInvariant() + "@example.test";
                var account = new UserAccount { Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(),
                    FirstName = "Fixture", Role = role, IsActive = true, AuthenticationStamp = Guid.NewGuid(), PasswordHash = string.Empty,
                    CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
                account.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(account, Password);
                _ids[role] = account.Id; db.Users.Add(account);
            }
            db.SaveChanges();
        });
    }

    [Theory]
    [InlineData(UserRoles.Admin, true, true, true)]
    [InlineData(UserRoles.Editor, true, false, false)]
    [InlineData(UserRoles.Advisor, true, false, false)]
    [InlineData(UserRoles.Service, false, true, false)]
    [InlineData(UserRoles.User, false, false, false)]
    public async Task RealJwtReadPermissionsAreSeparatedByArea(string role, bool content, bool support, bool users)
    {
        var token = await Login(role);
        foreach (var path in new[] { "site-content/homepage", "tour-contents", "tour-pages", "faqs" })
        {
            using var result = await Send(HttpMethod.Get, "/api/v1/admin/" + path, token);
            Assert.Equal(content ? HttpStatusCode.OK : HttpStatusCode.Forbidden, result.StatusCode);
        }
        foreach (var path in new[] { "feedback", "tickets", "ticket-errors" })
        {
            using var result = await Send(HttpMethod.Get, "/api/v1/admin/" + path, token);
            Assert.Equal(support ? HttpStatusCode.OK : HttpStatusCode.Forbidden, result.StatusCode);
        }
        using var list = await Send(HttpMethod.Get, "/api/v1/admin/users", token);
        Assert.Equal(users ? HttpStatusCode.OK : HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Theory]
    [InlineData(UserRoles.Admin, true)]
    [InlineData(UserRoles.Editor, true)]
    [InlineData(UserRoles.Advisor, true)]
    [InlineData(UserRoles.Service, false)]
    [InlineData(UserRoles.User, false)]
    public async Task ContentManagersCanSaveAndDeleteContentButOthersCannot(string role, bool allowed)
    {
        var token = await Login(role);
        using var homepage = await _client.GetAsync("/api/v1/site-content/homepage");
        using var document = JsonDocument.Parse(await homepage.Content.ReadAsStringAsync());
        using var save = await Send(HttpMethod.Put, "/api/v1/admin/site-content/homepage", token, document.RootElement);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, save.StatusCode);
        using var faq = await Send(HttpMethod.Post, "/api/v1/admin/faqs", token,
            new { questionTr = "Fixture soru?", questionEn = "Fixture question?", answerTr = "Fixture yanıt", answerEn = "Fixture answer", sortOrder = 0, isPublished = true });
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.Forbidden, faq.StatusCode);
        using var tour = await Send(HttpMethod.Put, "/api/v1/admin/tour-contents/1", token,
            new { titleTr = "Fixture", titleEn = "Fixture", sortOrder = 0, isVisible = true });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, tour.StatusCode);
        if (allowed)
        {
            using var body = JsonDocument.Parse(await faq.Content.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetInt32();
            using var deleted = await Send(HttpMethod.Delete, $"/api/v1/admin/faqs/{id}", token);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    [Theory]
    [InlineData(UserRoles.Editor)]
    [InlineData(UserRoles.Advisor)]
    [InlineData(UserRoles.Service)]
    [InlineData(UserRoles.User)]
    public async Task NonAdminsCannotAssignRolesCreateTicketsOrPatchTicketState(string role)
    {
        var token = await Login(role);
        using var promote = await Send(HttpMethod.Patch, $"/api/v1/admin/users/{_ids[role]}", token, new { role = UserRoles.Admin });
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);
        using var create = await Send(HttpMethod.Post, "/api/v1/admin/tickets", token, new { tourName = "Fixture", customerName = "Fixture", customerEmail = "fixture@example.test", guestCount = 1, amount = 350 });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        using var patch = await Send(HttpMethod.Patch, $"/api/v1/admin/tickets/{Guid.NewGuid()}", token, new { status = "Used" });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
    }

    [Theory]
    [InlineData(UserRoles.Admin, true)]
    [InlineData(UserRoles.Service, true)]
    [InlineData(UserRoles.Editor, false)]
    [InlineData(UserRoles.Advisor, false)]
    [InlineData(UserRoles.User, false)]
    public async Task OnlySupportRolesCanStartCancellationWithTheirOwnAuditIdentity(string role, bool allowed)
    {
        var token = await Login(role);
        using var result = await Send(HttpMethod.Post, $"/api/v1/admin/tickets/{Guid.NewGuid()}/cancel", token,
            new { ticketCode = "PRM-FIXTURE", expectedAmount = 350, reason = "Fixture only" });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(allowed ? _ids[role] : Guid.Empty, _cancellation.ActorId);
    }

    [Theory]
    [InlineData(UserRoles.Editor)]
    [InlineData(UserRoles.Advisor)]
    [InlineData(UserRoles.Service)]
    [InlineData(UserRoles.User)]
    public async Task AdminCannotDemoteOwnAccountToAnyNonAdminRole(string role)
    {
        var token = await Login(UserRoles.Admin);
        using var result = await Send(HttpMethod.Patch, $"/api/v1/admin/users/{_ids[UserRoles.Admin]}", token, new { role });
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        WithDb(db => Assert.Equal(UserRoles.Admin, db.Users.Single(user => user.Id == _ids[UserRoles.Admin]).Role));
    }

    [Theory]
    [InlineData(UserRoles.Editor)]
    [InlineData(UserRoles.Advisor)]
    [InlineData(UserRoles.Service)]
    public async Task RoleAssignmentAndRevocationInvalidatePreviousTokens(string role)
    {
        var admin = await Login(UserRoles.Admin); var old = await Login(UserRoles.User);
        using var assigned = await Send(HttpMethod.Patch, $"/api/v1/admin/users/{_ids[UserRoles.User]}", admin, new { role });
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        using var stale = await Send(HttpMethod.Get, "/api/v1/account/profile", old);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        var granted = await Login(UserRoles.User);
        var path = role == UserRoles.Service ? "/api/v1/admin/feedback" : "/api/v1/admin/site-content/homepage";
        using var allowed = await Send(HttpMethod.Get, path, granted); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var revoke = await Send(HttpMethod.Patch, $"/api/v1/admin/users/{_ids[UserRoles.User]}", admin, new { role = UserRoles.User });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var revoked = await Send(HttpMethod.Get, path, granted); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData(UserRoles.Editor)]
    [InlineData(UserRoles.Advisor)]
    [InlineData(UserRoles.Service)]
    public async Task RegistrationCannotRequestAPrivilegedRole(string role)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email = "new@example.test", password = Password, firstName = "Fixture", role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(UserRoles.User, body.RootElement.GetProperty("user").GetProperty("role").GetString());
    }

    private void WithDb(Action<PeremeToursDbContext> action)
    { using var scope = _factory.Services.CreateScope(); action(scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>()); }
    private async Task<string> Login(string role)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = role.ToLowerInvariant() + "@example.test", password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.GetProperty("accessToken").GetString()!;
    }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }
    private sealed class Cancellation : ITicketCancellationService
    {
        public Guid ActorId { get; private set; }
        public Task<TicketCancellationSummary?> CancelAsync(Guid ticketId, Guid actorUserId, CancelTicketCommand command, CancellationToken cancellationToken)
        { ActorId = actorUserId; return Task.FromResult<TicketCancellationSummary?>(new(ticketId, "Completed", 350, "Void", null, DateTime.UtcNow, DateTime.UtcNow, true)); }
        public Task<TicketCancellationSummary?> GetAsync(Guid ticketId, CancellationToken cancellationToken) => Task.FromResult<TicketCancellationSummary?>(null);
    }
    private sealed class Catalog : ITourCatalogService
    {
        public Task<IReadOnlyList<TourCatalogItem>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TourCatalogItem>>([new(1, 1, TourCategoryKeys.Sunset, "Sunset", "Fixture")]);
        public Task<IReadOnlyList<TourPort>> ListPortsAsync(int externalTourId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TourPort>>([]);
        public Task<TourAvailability?> GetAvailabilityAsync(int externalTourId, int externalDeparturePortId, int saleType, CancellationToken cancellationToken) => Task.FromResult<TourAvailability?>(null);
    }
    public void Dispose() { _client.Dispose(); _factory.Dispose(); _connection.Dispose(); }
}
