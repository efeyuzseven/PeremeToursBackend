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

public sealed class AdminUserProfileTests : IDisposable
{
    private const string Password = "FixturePassword1";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _bootstrapId = Guid.NewGuid();

    public AdminUserProfileTests()
    {
        _connection.Open();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-admin-profile-fixture-signing-key-2026");
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
            var guest = Account(_userId, "guest@example.test", UserRoles.User);
            db.Users.AddRange(Account(_adminId, "admin@example.test", UserRoles.Admin),
                guest, Account(_bootstrapId, "bootstrap@example.test", UserRoles.Admin));
            db.PasswordResetTokens.Add(new()
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                User = guest,
                TokenHash = new string('A', 64),
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
            });
            db.TourTickets.Add(new()
            {
                Id = Guid.NewGuid(),
                TicketCode = "PRM-FIXTURE-PROFILE",
                TourName = "Fixture Tour",
                CustomerName = "Original Guest",
                CustomerEmail = "guest@example.test",
                UserId = _userId,
                GuestCount = 1,
                Amount = 350,
                Currency = "TRY",
                TourDate = new DateOnly(2099, 10, 13),
                Status = TicketStatus.Confirmed,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        });
    }

    [Fact]
    public async Task AnonymousAndRegularUsersCannotEditOrListUsers()
    {
        using var anonymous = await Patch(_userId, new { firstName = "Changed" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var publicList = await _client.GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, publicList.StatusCode);
        var token = await Login("guest@example.test");
        using var forbidden = await Patch(_userId, new { firstName = "Changed" }, token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var forbiddenList = await Get("/api/v1/admin/users", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenList.StatusCode);
        Assert.Equal("Fixture", Snapshot(_userId).FirstName);
    }

    [Theory]
    [InlineData(UserRoles.Admin)]
    [InlineData(UserRoles.User)]
    [InlineData(UserRoles.Editor)]
    [InlineData(UserRoles.Advisor)]
    [InlineData(UserRoles.Service)]
    public async Task AdminCreatesAnyRoleWithoutChangingOwnSessionOrReturningSecrets(string role)
    {
        var token = await Login("admin@example.test");
        using var response = await Create(new { firstName = "  Created  ", lastName = " Person ",
            email = " NEW@EXAMPLE.TEST ", password = Password, role, id = _adminId, isActive = false, isDeleted = true }, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, raw, StringComparison.Ordinal);
        foreach (var secret in new[] { "passwordHash", "authenticationStamp", "accessToken", "password" })
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(raw);
        var id = body.RootElement.GetProperty("id").GetGuid(); Assert.NotEqual(_adminId, id);
        var created = Snapshot(id);
        Assert.Equal("new@example.test", created.Email); Assert.Equal("NEW@EXAMPLE.TEST", created.NormalizedEmail);
        Assert.Equal("Created", created.FirstName); Assert.Equal("Person", created.LastName); Assert.Equal(role, created.Role);
        Assert.True(created.IsActive); Assert.False(created.IsDeleted); Assert.NotNull(created.AuthenticationStamp);
        Assert.NotEqual(Password, created.PasswordHash);
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<UserAccount>().VerifyHashedPassword(created, created.PasswordHash, Password));
        var fresh = await Login("new@example.test");
        using var me = await Get("/api/v1/auth/me", fresh); using var identity = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(role, identity.RootElement.GetProperty("role").GetString());
        using var admin = await Get("/api/v1/admin/users", token); Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        WithDb(db => { Assert.Equal(4, db.Users.Count()); Assert.Single(db.PasswordResetTokens); Assert.Single(db.TourTickets); });
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(UserRoles.User, HttpStatusCode.Forbidden)]
    [InlineData(UserRoles.Editor, HttpStatusCode.Forbidden)]
    [InlineData(UserRoles.Advisor, HttpStatusCode.Forbidden)]
    [InlineData(UserRoles.Service, HttpStatusCode.Forbidden)]
    public async Task OnlyAdminsCanCreateAccountsIncludingOtherAdmins(string? role, HttpStatusCode expected)
    {
        string? token = null;
        if (role is not null)
        {
            WithDb(db => { db.Users.Single(user => user.Id == _userId).Role = role; db.SaveChanges(); });
            token = await Login("guest@example.test");
        }
        using var response = await Create(new { firstName = "Created", email = "new@example.test", password = Password, role = UserRoles.Admin }, token);
        Assert.Equal(expected, response.StatusCode); WithDb(db => Assert.Equal(3, db.Users.Count()));
    }

    [Theory]
    [InlineData("name")][InlineData("email")][InlineData("role")][InlineData("lowercase-role")]
    [InlineData("password")][InlineData("long-password")][InlineData("missing-role")]
    public async Task InvalidCreationNeverWritesAnAccount(string mode)
    {
        var payload = new Dictionary<string, object?> { ["firstName"] = "Created", ["lastName"] = "Person", ["email"] = "new@example.test", ["password"] = Password, ["role"] = UserRoles.User };
        switch (mode)
        {
            case "name": payload["firstName"] = "  "; break;
            case "email": payload["email"] = "invalid"; break;
            case "role": payload["role"] = "Owner"; break;
            case "lowercase-role": payload["role"] = "admin"; break;
            case "password": payload["password"] = "weakpassword"; break;
            case "long-password": payload["password"] = new string('A', 129) + "a1"; break;
            default: payload.Remove("role"); break;
        }
        using var response = await Create(payload, await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); WithDb(db => Assert.Equal(3, db.Users.Count()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateEmailCannotReplaceAnExistingAccountOrPassword(bool active)
    {
        WithDb(db => { db.Users.Single(user => user.Id == _userId).IsActive = active; db.SaveChanges(); });
        var before = Snapshot(_userId); var token = await Login("admin@example.test");
        using var response = await Create(new { firstName = "Must not replace", email = " GUEST@EXAMPLE.TEST ", password = "DifferentPassword2", role = UserRoles.Admin }, token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("USER_EMAIL_ALREADY_EXISTS", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var after = Snapshot(_userId); Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.FirstName, after.FirstName); Assert.Equal(before.Role, after.Role); Assert.Equal(active, after.IsActive);
        WithDb(db => Assert.Equal(3, db.Users.Count()));
    }

    [Fact]
    public async Task CreateChecksCurrentActorRoleAndAuthenticationStampInsideTransaction()
    {
        var actor = Snapshot(_adminId);
        WithDb(db => { db.Users.Single(user => user.Id == _adminId).AuthenticationStamp = Guid.NewGuid(); db.SaveChanges(); });
        using var scope = _factory.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        var result = await service.CreateAsync(_adminId, actor.AuthenticationStamp?.ToString("N"), new("Created", null, "new@example.test", Password, UserRoles.Admin), CancellationToken.None);
        Assert.Equal(CreateUserStatus.Forbidden, result.Status); WithDb(db => Assert.Equal(3, db.Users.Count()));
    }

    [Fact]
    public async Task NewAccountNeverInheritsReservationsByContactEmail()
    {
        WithDb(db => { db.TourTickets.Single().CustomerEmail = "new@example.test"; db.SaveChanges(); });
        using var response = await Create(new { firstName = "Created", email = "new@example.test", password = Password, role = UserRoles.User }, await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var reservations = await Get("/api/v1/account/reservations", await Login("new@example.test"));
        Assert.Equal("[]", await reservations.Content.ReadAsStringAsync());
        WithDb(db => Assert.Equal(_userId, db.TourTickets.Single().UserId));
    }

    [Fact]
    public async Task ProfileEditingPreservesPasswordRoleAndBookingOwnership()
    {
        var before = Snapshot(_userId);
        var token = await Login("admin@example.test");
        using var response = await Patch(_userId, new { firstName = " Updated ", lastName = " Name ", email = "NEW.GUEST@EXAMPLE.TEST" }, token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var after = Snapshot(_userId);
        Assert.Equal("Updated", after.FirstName); Assert.Equal("Name", after.LastName);
        Assert.Equal("new.guest@example.test", after.Email); Assert.Equal("NEW.GUEST@EXAMPLE.TEST", after.NormalizedEmail);
        Assert.Equal(before.PasswordHash, after.PasswordHash); Assert.Equal(before.Role, after.Role); Assert.Equal(before.IsActive, after.IsActive);
        WithDb(db =>
        {
            var booking = db.TourTickets.Single();
            Assert.Equal(_userId, booking.UserId); Assert.Equal("guest@example.test", booking.CustomerEmail);
            Assert.Equal("Original Guest", booking.CustomerName); Assert.Equal(350, booking.Amount); Assert.Equal(TicketStatus.Confirmed, booking.Status);
        });
    }

    [Fact]
    public async Task EmailChangeRevokesRealJwtAndOldResetLinksButSamePasswordStillWorks()
    {
        var guestToken = await Login("guest@example.test");
        var adminToken = await Login("admin@example.test");
        using var edited = await Patch(_userId, new { email = "updated@example.test" }, adminToken);
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        using var me = await Get("/api/v1/auth/me", guestToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var oldLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "guest@example.test", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var newToken = await Login("updated@example.test");
        using var newMe = await Get("/api/v1/auth/me", newToken);
        Assert.Equal(HttpStatusCode.OK, newMe.StatusCode);
        WithDb(db => Assert.NotNull(db.PasswordResetTokens.Single().ConsumedAtUtc));
    }

    [Theory]
    [InlineData("firstName", "New name")]
    [InlineData("email", "GUEST@EXAMPLE.TEST")]
    [InlineData("lastName", "")]
    public async Task NamesAndCaseOnlyEmailChangesPreserveSessionsAndResetLinks(string field, string value)
    {
        var before = Snapshot(_userId);
        var guestToken = await Login("guest@example.test");
        using var edited = await Patch(_userId, new Dictionary<string, string> { [field] = value }, await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        using var me = await Get("/api/v1/auth/me", guestToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(before.AuthenticationStamp, Snapshot(_userId).AuthenticationStamp);
        WithDb(db => Assert.Null(db.PasswordResetTokens.Single().ConsumedAtUtc));
        if (field == "lastName") Assert.Null(Snapshot(_userId).LastName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateEmailsIncludingInactiveAccountsNeverPartiallyUpdate(bool active)
    {
        var before = Snapshot(_userId);
        WithDb(db => { db.Users.Single(user => user.Id == _bootstrapId).IsActive = active; db.SaveChanges(); });
        using var response = await Patch(_userId, new { firstName = "Must not save", email = "BOOTSTRAP@EXAMPLE.TEST", role = "Admin" }, await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("USER_EMAIL_ALREADY_EXISTS", json.RootElement.GetProperty("title").GetString());
        var after = Snapshot(_userId);
        Assert.Equal(before.FirstName, after.FirstName); Assert.Equal(before.Role, after.Role);
        Assert.Equal(before.Email, after.Email); Assert.Equal(before.AuthenticationStamp, after.AuthenticationStamp);
        WithDb(db => Assert.Null(db.PasswordResetTokens.Single().ConsumedAtUtc));
    }

    [Theory]
    [InlineData("firstName", "")]
    [InlineData("firstName", "   ")]
    [InlineData("email", "bad-address")]
    [InlineData("email", "")]
    [InlineData("role", "SuperAdmin")]
    public async Task InvalidProfilesLeaveTheAccountUntouched(string field, string value)
    {
        var before = Snapshot(_userId);
        using var response = await Patch(_userId, new Dictionary<string, string> { [field] = value }, await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = Snapshot(_userId);
        Assert.Equal(before.FirstName, after.FirstName); Assert.Equal(before.Email, after.Email); Assert.Equal(before.Role, after.Role);
    }

    [Theory]
    [InlineData("firstName", 101)]
    [InlineData("lastName", 101)]
    [InlineData("email", 321)]
    public async Task OverlongFieldsAreRejected(string field, int length)
    {
        using var response = await Patch(_userId, new Dictionary<string, string> { [field] = new string('a', length) }, await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BootstrapEmailIsProtectedAndItsNamesRemainEditable()
    {
        var token = await Login("bootstrap@example.test");
        using var protectedEdit = await Patch(_bootstrapId, new { email = "different@example.test", firstName = "Must not save" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, protectedEdit.StatusCode);
        using var json = JsonDocument.Parse(await protectedEdit.Content.ReadAsStringAsync());
        Assert.Equal("USER_EMAIL_PROTECTED", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("Fixture", Snapshot(_bootstrapId).FirstName);
        using var nameEdit = await Patch(_bootstrapId, new { firstName = "Administrator" }, token);
        Assert.Equal(HttpStatusCode.NoContent, nameEdit.StatusCode);
        using var me = await Get("/api/v1/auth/me", token); Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task ListIsUncachedAndExposesOnlyEditableProfileData()
    {
        using var response = await Get("/api/v1/admin/users", await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var users = json.RootElement.EnumerateArray().ToList(); Assert.Equal(3, users.Count);
        Assert.False(users.Single(user => user.GetProperty("id").GetGuid() == _bootstrapId).GetProperty("canEditEmail").GetBoolean());
        Assert.True(users.Single(user => user.GetProperty("id").GetGuid() == _userId).GetProperty("canEditEmail").GetBoolean());
        Assert.All(users, user => { Assert.False(user.TryGetProperty("passwordHash", out _)); Assert.False(user.TryGetProperty("authenticationStamp", out _)); });
    }

    [Fact]
    public async Task OwnProfileCanChangeButCannotSelfDemoteOrDisable()
    {
        var token = await Login("admin@example.test");
        using var demote = await Patch(_adminId, new { role = "User", firstName = "Must not save" }, token);
        using var disable = await Patch(_adminId, new { isActive = false }, token);
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);
        Assert.Equal("Fixture", Snapshot(_adminId).FirstName);
        using var emailEdit = await Patch(_adminId, new { email = "new.admin@example.test" }, token);
        Assert.Equal(HttpStatusCode.NoContent, emailEdit.StatusCode);
        using var oldAccess = await Get("/api/v1/admin/users", token); Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);
        using var newAccess = await Get("/api/v1/admin/users", await Login("new.admin@example.test")); Assert.Equal(HttpStatusCode.OK, newAccess.StatusCode);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("isActive")]
    public async Task AccessChangesRevokeTokensEvenIfAccessIsRestored(string field)
    {
        var token = await Login("guest@example.test");
        var admin = await Login("admin@example.test");
        object first = field == "role" ? new { role = "Admin" } : new { isActive = false };
        object second = field == "role" ? new { role = "User" } : new { isActive = true };
        using var changed = await Patch(_userId, first, admin); Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        using var restored = await Patch(_userId, second, admin); Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        using var oldMe = await Get("/api/v1/auth/me", token); Assert.Equal(HttpStatusCode.Unauthorized, oldMe.StatusCode);
    }

    [Fact]
    public async Task MissingTargetIs404AndDatabaseActorCheckCannotBeBypassed()
    {
        using var missing = await Patch(Guid.NewGuid(), new { firstName = "Updated" }, await Login("admin@example.test"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IUserAdminService>();
        Assert.Equal(UpdateUserStatus.Forbidden, await service.UpdateAsync(_userId, _adminId, new(null, null, "Must not save"), CancellationToken.None));
        var db = scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>();
        db.Users.Single(user => user.Id == _adminId).IsActive = false; await db.SaveChangesAsync();
        Assert.Equal(UpdateUserStatus.Forbidden, await service.UpdateAsync(_adminId, _userId, new(null, null, "Must not save"), CancellationToken.None));
        Assert.Equal("Fixture", Snapshot(_userId).FirstName);
    }

    private static UserAccount Account(Guid id, string email, string role)
    {
        var user = new UserAccount
        {
            Id = id,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = "Fixture",
            LastName = "Guest",
            PasswordHash = string.Empty,
            Role = role,
            IsActive = true,
            AuthenticationStamp = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        user.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(user, Password); return user;
    }
    private void WithDb(Action<PeremeToursDbContext> action)
    { using var scope = _factory.Services.CreateScope(); action(scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>()); }
    private UserAccount Snapshot(Guid id)
    { using var scope = _factory.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>().Users.AsNoTracking().Single(user => user.Id == id); }
    private async Task<string> Login(string email)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("accessToken").GetString()!;
    }
    private async Task<HttpResponseMessage> Patch(Guid id, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/users/{id}") { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
    private async Task<HttpResponseMessage> Create(object body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users") { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
    private async Task<HttpResponseMessage> Get(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
    public void Dispose() { _client.Dispose(); _factory.Dispose(); _connection.Dispose(); }
}
