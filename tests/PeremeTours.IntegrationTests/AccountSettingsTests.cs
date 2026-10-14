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
using PeremeTours.Application.Users;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.IntegrationTests;

public sealed class AccountSettingsTests : IDisposable
{
    private const string Password = "FixturePassword1";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _bootstrapId = Guid.NewGuid();

    public AccountSettingsTests()
    {
        _connection.Open();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-account-settings-fixture-signing-key-2026");
            builder.UseSetting("MailSettings:Enabled", "false");
            builder.UseSetting("DeploymentBootstrap:AdminEmail", "bootstrap@example.test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<PeremeToursDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<PeremeToursDbContext>>();
                services.AddDbContext<PeremeToursDbContext>(options => options.UseSqlite(_connection)
                    .ReplaceService<IModelCustomizer, PasswordResetEndpointTests.TestModel>());
            });
        });
        _client = _factory.CreateClient();
        WithDb(db =>
        {
            db.Database.EnsureCreated();
            var user = Account(_userId, "guest@example.test", UserRoles.User);
            db.Users.AddRange(user, Account(_adminId, "admin@example.test", UserRoles.Admin),
                Account(_bootstrapId, "bootstrap@example.test", UserRoles.Admin));
            db.PasswordResetTokens.Add(new() { Id = Guid.NewGuid(), UserId = _userId, User = user,
                TokenHash = new string('A', 64), AuthenticationStamp = user.AuthenticationStamp,
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30) });
            db.TourTickets.Add(new() { Id = Guid.NewGuid(), TicketCode = "PRM-FIXTURE-OWNED", TourName = "Fixture Tour",
                CustomerName = "Original Guest", CustomerEmail = "guest@example.test", UserId = _userId,
                GuestCount = 1, Amount = 350, Currency = "TRY", TourDate = new DateOnly(2099, 10, 14),
                Status = TicketStatus.Confirmed, PaymentStatus = TicketPaymentStatus.Paid, TicketingStatus = TicketingStatus.Issued,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            db.SaveChanges();
        });
    }

    [Theory]
    [InlineData("profile", "PATCH")]
    [InlineData("password", "POST")]
    [InlineData("", "DELETE")]
    public async Task AnonymousCannotChangeAccount(string path, string method)
    {
        using var response = await Send(new(method), "/api/v1/account" + (path.Length == 0 ? "" : "/" + path),
            new { firstName = "Changed", email = "new@example.test", currentPassword = Password, newPassword = "ChangedPassword2", confirmed = true });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(Snapshot(_userId).IsDeleted);
    }

    [Fact]
    public async Task NameUpdateIgnoresInjectedOwnerRoleAndAccessFields()
    {
        var token = await Login("guest@example.test"); var before = Snapshot(_userId);
        using var response = await Send(HttpMethod.Patch, "/api/v1/account/profile", new {
            firstName = "  Updated  ", lastName = " Person ", email = "guest@example.test",
            userId = _adminId, role = "Admin", isActive = false, isDeleted = true }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var after = Snapshot(_userId);
        Assert.Equal("Updated", after.FirstName); Assert.Equal("Person", after.LastName);
        Assert.Equal(before.PasswordHash, after.PasswordHash); Assert.Equal(before.AuthenticationStamp, after.AuthenticationStamp);
        Assert.Equal(UserRoles.User, after.Role); Assert.True(after.IsActive); Assert.False(after.IsDeleted);
        Assert.Equal("Fixture", Snapshot(_adminId).FirstName);
        using var profile = await Send(HttpMethod.Get, "/api/v1/account/profile", token: token);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var body = await profile.Content.ReadAsStringAsync(); Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authenticationStamp", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("WrongPassword2")]
    public async Task EmailChangesRequireCorrectCurrentPassword(string? password)
    {
        var token = await Login("guest@example.test");
        using var response = await Send(HttpMethod.Patch, "/api/v1/account/profile",
            new { firstName = "Changed", email = "new@example.test", currentPassword = password }, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CURRENT_PASSWORD_INCORRECT", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("Fixture", Snapshot(_userId).FirstName);
    }

    [Fact]
    public async Task EmailChangeInvalidatesOtherSessionsAndResetLinksWithoutReassigningBookings()
    {
        var token = await Login("guest@example.test"); var otherSession = await Login("guest@example.test");
        using var response = await Send(HttpMethod.Patch, "/api/v1/account/profile", new {
            firstName = "Updated", email = " NEW@EXAMPLE.TEST ", currentPassword = Password }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.True(json.RootElement.GetProperty("requiresLogin").GetBoolean());
        using var stale = await Send(HttpMethod.Get, "/api/v1/account/profile", token: otherSession);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        AssertResetLinksConsumed(); AssertBookingPreserved();
        using var oldLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "guest@example.test", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var fresh = await Login("new@example.test");
        using var owned = await Send(HttpMethod.Get, "/api/v1/account/reservations", token: fresh);
        Assert.Contains("PRM-FIXTURE-OWNED", await owned.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateEmailIsRejectedWithoutPartialChangesIncludingInactiveUsers()
    {
        WithDb(db => { db.Users.Single(user => user.Id == _adminId).IsActive = false; db.SaveChanges(); });
        var token = await Login("guest@example.test");
        using var response = await Send(HttpMethod.Patch, "/api/v1/account/profile",
            new { firstName = "Changed", email = "admin@example.test", currentPassword = Password }, token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal("Fixture", Snapshot(_userId).FirstName);
    }

    [Theory]
    [InlineData("WrongPassword2", "ChangedPassword2")]
    [InlineData(Password, "weak")]
    [InlineData(Password, "lowercase123")]
    public async Task InvalidPasswordChangesDoNotRevokeCurrentAccount(string currentPassword, string newPassword)
    {
        var token = await Login("guest@example.test"); var before = Snapshot(_userId);
        using var response = await Send(HttpMethod.Post, "/api/v1/account/password", new { currentPassword, newPassword }, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before.PasswordHash, Snapshot(_userId).PasswordHash);
        Assert.Equal(before.AuthenticationStamp, Snapshot(_userId).AuthenticationStamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordChangeRevokesAllSessionsIncludingLegacyTokens(bool legacy)
    {
        if (legacy) WithDb(db => { db.Users.Single(user => user.Id == _userId).AuthenticationStamp = null; db.SaveChanges(); });
        var token = await Login("guest@example.test"); var otherSession = await Login("guest@example.test");
        using var response = await Send(HttpMethod.Post, "/api/v1/account/password",
            new { currentPassword = Password, newPassword = "ChangedPassword2" }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); AssertResetLinksConsumed(); AssertBookingPreserved();
        Assert.NotEqual("ChangedPassword2", Snapshot(_userId).PasswordHash);
        using var stale = await Send(HttpMethod.Get, "/api/v1/account/profile", token: otherSession);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        using var oldLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "guest@example.test", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        await Login("guest@example.test", "ChangedPassword2");
    }

    [Theory]
    [InlineData(false, Password)]
    [InlineData(true, "WrongPassword2")]
    public async Task DeletionRequiresConfirmationAndPassword(bool confirmed, string currentPassword)
    {
        var token = await Login("guest@example.test");
        using var response = await Send(HttpMethod.Delete, "/api/v1/account", new { confirmed, currentPassword }, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.False(Snapshot(_userId).IsDeleted);
    }

    [Fact]
    public async Task DeletionAnonymizesProfilePreservesPaidTicketsAndCannotBeReactivated()
    {
        var token = await Login("guest@example.test"); var otherSession = await Login("guest@example.test");
        using var response = await Send(HttpMethod.Delete, "/api/v1/account", new { currentPassword = Password, confirmed = true }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var account = Snapshot(_userId);
        Assert.True(account.IsDeleted); Assert.False(account.IsActive); Assert.NotNull(account.DeletedAtUtc);
        Assert.Equal(string.Empty, account.PasswordHash); Assert.Null(account.LastName);
        Assert.NotEqual("guest@example.test", account.Email); AssertResetLinksConsumed(); AssertBookingPreserved();
        using var stale = await Send(HttpMethod.Get, "/api/v1/account/reservations", token: otherSession);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        using var oldLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "guest@example.test", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var admin = await Login("admin@example.test");
        using var restore = await Send(HttpMethod.Patch, $"/api/v1/admin/users/{_userId}", new { isActive = true }, admin);
        Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);
        using var users = await Send(HttpMethod.Get, "/api/v1/admin/users", token: admin);
        Assert.DoesNotContain(_userId.ToString(), await users.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var registration = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email = "guest@example.test", password = Password, firstName = "New" });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        using var registered = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        Assert.NotEqual(_userId, registered.RootElement.GetProperty("user").GetProperty("id").GetGuid());
        var fresh = registered.RootElement.GetProperty("accessToken").GetString()!;
        using var owned = await Send(HttpMethod.Get, "/api/v1/account/reservations", token: fresh);
        Assert.Equal("[]", await owned.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BootstrapEmailAndDeletionAreProtectedButPasswordCanBeChanged()
    {
        var token = await Login("bootstrap@example.test");
        using var profile = await Send(HttpMethod.Get, "/api/v1/account/profile", token: token);
        using var json = JsonDocument.Parse(await profile.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("canEditEmail").GetBoolean()); Assert.False(json.RootElement.GetProperty("canDeleteAccount").GetBoolean());
        using var email = await Send(HttpMethod.Patch, "/api/v1/account/profile", new { firstName = "Fixture", email = "new@example.test", currentPassword = Password }, token);
        Assert.Equal(HttpStatusCode.Conflict, email.StatusCode);
        using var deletion = await Send(HttpMethod.Delete, "/api/v1/account", new { currentPassword = Password, confirmed = true }, token);
        Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);
        using var password = await Send(HttpMethod.Post, "/api/v1/account/password", new { currentPassword = Password, newPassword = "ChangedPassword2" }, token);
        Assert.Equal(HttpStatusCode.OK, password.StatusCode);
    }

    [Fact]
    public async Task LastActiveAdministratorCannotDeleteTheirAccount()
    {
        WithDb(db => { db.Users.Single(user => user.Id == _bootstrapId).IsActive = false; db.SaveChanges(); });
        var token = await Login("admin@example.test");
        using var profile = await Send(HttpMethod.Get, "/api/v1/account/profile", token: token);
        Assert.Contains("LAST_ADMINISTRATOR", await profile.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var deletion = await Send(HttpMethod.Delete, "/api/v1/account", new { currentPassword = Password, confirmed = true }, token);
        Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode); Assert.False(Snapshot(_adminId).IsDeleted);
    }

    [Fact]
    public async Task ServiceRejectsStampThatChangedSinceMiddlewareValidation()
    {
        var staleStamp = Snapshot(_userId).AuthenticationStamp?.ToString("N");
        WithDb(db => { db.Users.Single(user => user.Id == _userId).AuthenticationStamp = Guid.NewGuid(); db.SaveChanges(); });
        using var scope = _factory.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<IAccountManagementService>();
        Assert.Equal(AccountOperationStatus.Unauthorized, (await service.ChangePasswordAsync(_userId, staleStamp, Password, "ChangedPassword2", CancellationToken.None)).Status);
        Assert.Equal(AccountOperationStatus.Unauthorized, (await service.DeleteAccountAsync(_userId, staleStamp, Password, true, CancellationToken.None)).Status);
        Assert.False(Snapshot(_userId).IsDeleted);
    }

    private static UserAccount Account(Guid id, string email, string role)
    {
        var user = new UserAccount { Id = id, Email = email, NormalizedEmail = email.ToUpperInvariant(), FirstName = "Fixture",
            LastName = "Guest", PasswordHash = string.Empty, Role = role, IsActive = true, AuthenticationStamp = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        user.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(user, Password); return user;
    }
    private void WithDb(Action<PeremeToursDbContext> action)
    { using var scope = _factory.Services.CreateScope(); action(scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>()); }
    private UserAccount Snapshot(Guid id)
    { using var scope = _factory.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>().Users.AsNoTracking().Single(user => user.Id == id); }
    private void AssertResetLinksConsumed() => WithDb(db => Assert.All(db.PasswordResetTokens.Where(token => token.UserId == _userId).ToList(), token => Assert.NotNull(token.ConsumedAtUtc)));
    private void AssertBookingPreserved() => WithDb(db => {
        var ticket = db.TourTickets.Single(); Assert.Equal(_userId, ticket.UserId); Assert.Equal(TicketPaymentStatus.Paid, ticket.PaymentStatus);
        Assert.Equal(TicketStatus.Confirmed, ticket.Status); Assert.Equal("Original Guest", ticket.CustomerName);
        Assert.Equal("guest@example.test", ticket.CustomerEmail); Assert.Empty(db.TicketCancellations);
    });
    private async Task<string> Login(string email, string password = Password)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("accessToken").GetString()!;
    }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
    public void Dispose() { _client.Dispose(); _factory.Dispose(); _connection.Dispose(); }
}
