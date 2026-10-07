using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.IntegrationTests;

public sealed class AccountAndCancellationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _inactive = Guid.NewGuid();
    private readonly Guid _ticketId = Guid.NewGuid();
    public AccountAndCancellationTests()
    {
        _connection.Open();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-account-test-signing-key-2026-security");
            builder.UseSetting("MailSettings:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<PeremeToursDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<PeremeToursDbContext>>();
                services.AddDbContext<PeremeToursDbContext>(options => options.UseSqlite(_connection).ReplaceService<IModelCustomizer, TestModel>());
                services.AddScoped<ITicketCancellationService, StubCancellation>();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            });
        });
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>();
        db.Database.EnsureCreated();
        var owner = Person(_owner, "User"); owner.Email = "same@example.test"; owner.NormalizedEmail = "SAME@EXAMPLE.TEST";
        db.Users.AddRange(owner, Person(_other, "User"), Person(_admin, "Admin"), Person(_inactive, "User", false));
        db.TourTickets.AddRange(Ticket(_ticketId, _owner, "PRM-OWN"), Ticket(Guid.NewGuid(), _other, "PRM-OTHER"), Ticket(Guid.NewGuid(), null, "PRM-GUEST"));
        db.SaveChanges();
    }

    [Fact]
    public async Task OwnReservationsExcludeOthersAndGuestOrdersEvenWhenContactEmailMatches()
    {
        using var response = await GetAsync("/api/v1/account/reservations", _owner, "User");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PRM-OWN", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PRM-OTHER", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PRM-GUEST", body, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", body, StringComparison.Ordinal);
        Assert.Contains("22222222-2222-4222-8222-222222222222", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountProfileExcludesPasswordAndAdminAccountDoesNotListOtherUsers()
    {
        using var profile = await GetAsync("/api/v1/account/profile", _owner, "User");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.DoesNotContain("password", await profile.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var reservations = await GetAsync("/api/v1/account/reservations", _admin, "Admin");
        Assert.Equal("[]", await reservations.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CancelledBookingsHaveNoQr()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>();
            db.TicketCancellations.Add(new TicketCancellation { TicketId = _ticketId, ActorUserId = _admin, Reason = "Test", Amount = 350,
                Status = TicketCancellationStatus.ReviewRequired, RequestedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            db.SaveChanges();
        }
        using var response = await GetAsync("/api/v1/account/reservations", _owner, "User");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("22222222-2222-4222-8222-222222222222", body, StringComparison.Ordinal);
        Assert.Contains("ReviewRequired", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("reservations")]
    public async Task AnonymousAndInactiveUsersCannotReadAccount(string endpoint)
    {
        using var anonymous = await _client.GetAsync("/api/v1/account/" + endpoint);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var inactive = await GetAsync("/api/v1/account/" + endpoint, _inactive, "User");
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task CancellationRequiresAnActiveRealAdmin(string? role, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/tickets/{_ticketId}/cancel")
        { Content = JsonContent.Create(new { ticketCode = "PRM-OWN", expectedAmount = 350, reason = "Test" }) };
        if (role is not null) { request.Headers.Add("X-Test-Role", role); request.Headers.Add("X-Test-User", _admin.ToString()); }
        using var response = await _client.SendAsync(request); Assert.Equal(expected, response.StatusCode);
        if (role == "Admin")
        {
            request.Headers.Remove("X-Test-User"); // A stale JWT role is not enough after admin access is revoked.
            using var stale = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/tickets/{_ticketId}/cancel")
            { Content = JsonContent.Create(new { ticketCode = "PRM-OWN", expectedAmount = 350, reason = "Test" }) };
            stale.Headers.Add("X-Test-Role", "Admin"); stale.Headers.Add("X-Test-User", _owner.ToString());
            using var forbidden = await _client.SendAsync(stale); Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
    }

    private Task<HttpResponseMessage> GetAsync(string path, Guid user, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Add("X-Test-User", user.ToString()); request.Headers.Add("X-Test-Role", role);
        return _client.SendAsync(request);
    }
    private static UserAccount Person(Guid id, string role, bool active = true) => new() { Id = id, Email = $"{id}@example.test",
        NormalizedEmail = $"{id}@EXAMPLE.TEST", FirstName = "Demo", PasswordHash = "not-a-real-password-hash", Role = role, IsActive = active, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
    private static TourTicket Ticket(Guid id, Guid? owner, string code) => new() { Id = id, UserId = owner, TicketCode = code,
        CustomerEmail = "same@example.test", CustomerName = "Demo", TourName = "Sunset", Currency = "TRY", GuestCount = 1, Amount = 350,
        TourDate = new DateOnly(2099, 10, 8), Status = TicketStatus.Confirmed, PaymentStatus = TicketPaymentStatus.Paid, TicketingStatus = TicketingStatus.Issued,
        CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow, Passengers = [new TourPassenger { Id = Guid.NewGuid(), FirstName = "Demo", LastName = "Guest",
            Gender = "male", Nationality = "TR", IdentityNumber = "12345678901", Pnr = "MOCKPNR", ExternalTicketGuid = "22222222-2222-4222-8222-222222222222" }] };
    public sealed class TestModel(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        { base.Customize(modelBuilder, context); foreach (var entity in modelBuilder.Model.GetEntityTypes()) foreach (var property in entity.GetProperties())
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(new DateTimeOffsetToBinaryConverter()); }
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        { var role = Request.Headers["X-Test-Role"].ToString(); if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].ToString())], "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test"))); }
    }
    private sealed class StubCancellation : ITicketCancellationService
    {
        public Task<TicketCancellationSummary?> CancelAsync(Guid ticketId, Guid actorUserId, CancelTicketCommand command, CancellationToken cancellationToken)
            => Task.FromResult<TicketCancellationSummary?>(new(ticketId, "Completed", 350, "Void", null, DateTime.UtcNow, DateTime.UtcNow, true));
        public Task<TicketCancellationSummary?> GetAsync(Guid ticketId, CancellationToken cancellationToken) => Task.FromResult<TicketCancellationSummary?>(null);
    }
    public void Dispose() { _client.Dispose(); _factory.Dispose(); _connection.Dispose(); }
}
