using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Authentication;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Authentication;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.UnitTests;

public sealed class PasswordResetTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly PasswordHasher<UserAccount> _hasher = new();
    private readonly Sender _sender = new();
    private readonly Clock _clock = new();
    private readonly UserAccount _user;
    private readonly MailOptions _mail = new() { Enabled = true };
    public PasswordResetTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, TestModel>().Options);
        _db.Database.EnsureCreated();
        _user = new() { Id = Guid.NewGuid(), Email = "reset@example.test", NormalizedEmail = "RESET@EXAMPLE.TEST",
            FirstName = "Fixture", PasswordHash = string.Empty, Role = UserRoles.Admin, IsActive = true,
            CreatedAtUtc = _clock.GetUtcNow(), UpdatedAtUtc = _clock.GetUtcNow() };
        _user.PasswordHash = _hasher.HashPassword(_user, "OriginalPassword1");
        _db.Users.Add(_user); _db.SaveChanges();
    }
    private PasswordResetService Service => new(_db, _hasher, _sender, Options.Create(_mail), _clock);
    private Task Request(string email = "reset@example.test", string language = "tr") =>
        Service.RequestAsync(new(email, language), CancellationToken.None);

    [Fact]
    public async Task RequestsStoreOnlyHashAndUseCryptographicSingleUseTokens()
    {
        await Request(" RESET@EXAMPLE.TEST ", "en");
        var sent = Assert.Single(_sender.Messages);
        var record = await _db.PasswordResetTokens.SingleAsync();
        Assert.Equal(64, sent.Token.Length); Assert.True(sent.Token.All(Uri.IsHexDigit));
        Assert.NotEqual(sent.Token, record.TokenHash);
        Assert.Equal(PasswordResetService.HashToken(sent.Token), record.TokenHash);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(30), record.ExpiresAtUtc);
        Assert.Equal("en", sent.Language); Assert.Equal(_user.Email, sent.Email);
    }

    [Theory]
    [InlineData("unknown@example.test", true, true)]
    [InlineData("reset@example.test", false, true)]
    [InlineData("reset@example.test", true, false)]
    public async Task UnknownInactiveAndDisabledMailDoNotSend(string email, bool active, bool enabled)
    {
        _user.IsActive = active; _mail.Enabled = enabled; await _db.SaveChangesAsync();
        await Request(email);
        Assert.Empty(_sender.Messages); Assert.Empty(await _db.PasswordResetTokens.ToListAsync());
    }

    [Fact]
    public async Task DeliveryIsLimitedPerAccountAndNeverRepeatedWithinOneMinute()
    {
        await Request(); await Request(); Assert.Single(_sender.Messages);
        _clock.Advance(TimeSpan.FromMinutes(2)); await Request();
        _clock.Advance(TimeSpan.FromMinutes(2)); await Request();
        _clock.Advance(TimeSpan.FromMinutes(2)); await Request();
        Assert.Equal(3, _sender.Messages.Count);
        _clock.Advance(TimeSpan.FromHours(1)); await Request(); Assert.Equal(4, _sender.Messages.Count);
    }

    [Fact]
    public async Task ResetChangesHashAndStampInvalidatesEveryOtherLinkButPreservesRole()
    {
        await Request(); var first = _sender.Messages[0].Token;
        _clock.Advance(TimeSpan.FromMinutes(2)); await Request(); var second = _sender.Messages[1].Token;
        var originalHash = _user.PasswordHash;
        Assert.True(await Service.ResetAsync(first, "NewPassword123", CancellationToken.None));
        _db.ChangeTracker.Clear(); var account = await _db.Users.SingleAsync();
        Assert.NotNull(account.AuthenticationStamp); Assert.NotEqual(originalHash, account.PasswordHash);
        Assert.Equal(UserRoles.Admin, account.Role);
        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(account, account.PasswordHash, "NewPassword123"));
        Assert.False(await Service.ResetAsync(first, "OtherPassword123", CancellationToken.None));
        Assert.False(await Service.ResetAsync(second, "OtherPassword123", CancellationToken.None));
        Assert.All(await _db.PasswordResetTokens.ToListAsync(), item => Assert.NotNull(item.ConsumedAtUtc));
    }

    [Fact]
    public async Task ExpiredLinksCannotChangePasswords()
    {
        await Request(); _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.False(await Service.ResetAsync(_sender.Messages[0].Token, "NewPassword123", CancellationToken.None));
        Assert.Null((await _db.Users.AsNoTracking().SingleAsync()).AuthenticationStamp);
    }

    [Fact]
    public async Task DeactivatedAccountsCannotReset()
    {
        await Request(); _user.IsActive = false; await _db.SaveChangesAsync();
        Assert.False(await Service.ResetAsync(_sender.Messages[0].Token, "NewPassword123", CancellationToken.None));
    }

    [Fact]
    public async Task ChangedAuthenticationStampInvalidatesPreviouslyIssuedLink()
    {
        await Request(); _user.AuthenticationStamp = Guid.NewGuid(); await _db.SaveChangesAsync();
        Assert.False(await Service.ResetAsync(_sender.Messages[0].Token, "NewPassword123", CancellationToken.None));
    }

    [Theory]
    [InlineData("short", "NewPassword123")]
    [InlineData("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", "NewPassword123")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "lowercaseonly")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "short1")]
    public async Task MalformedUnknownOrWeakRequestsDoNotChangePasswords(string token, string password)
    {
        Assert.False(await Service.ResetAsync(token, password, CancellationToken.None));
        Assert.Null((await _db.Users.AsNoTracking().SingleAsync()).AuthenticationStamp);
    }

    [Theory]
    [InlineData("tr", "Şifre sıfırlama", "30 dakika")]
    [InlineData("en", "Reset your password", "30 minutes")]
    public void ResetEmailUsesOnlyPrimaryDomainAndTokenFragment(string language, string subject, string expiry)
    {
        var token = new string('A', 64);
        var rendered = PasswordResetEmailTemplate.Render(token, language);
        Assert.Contains(subject, rendered.Subject, StringComparison.Ordinal);
        Assert.Contains("https://pereme.com.tr/sifre-sifirla#token=" + token, rendered.Html, StringComparison.Ordinal);
        Assert.Contains(expiry, rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", rendered.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudfront.net", rendered.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestBufferIsBoundedAndDoesNotSilentlyDropAcceptedRequests()
    {
        var queue = new PasswordResetRequestBuffer();
        for (var index = 0; index < 128; index++) Assert.True(queue.TryEnqueue(new("unknown@example.test", "tr")));
        Assert.False(queue.TryEnqueue(new("unknown@example.test", "tr")));
    }

    [Fact]
    public async Task LoginCanUpgradeLegacyPasswordHashWithoutChangingRoleOrStamp()
    {
        var legacy = new PasswordHasher<UserAccount>(Options.Create(new PasswordHasherOptions
            { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
        _user.PasswordHash = legacy.HashPassword(_user, "OriginalPassword1"); await _db.SaveChangesAsync();
        var service = new AuthenticationService(_db, _hasher, new AccessTokens());
        var login = await service.LoginAsync(new("reset@example.test", "OriginalPassword1"), CancellationToken.None);
        Assert.NotNull(login); Assert.Equal(UserRoles.Admin, login.User.Role);
        var saved = await _db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(saved, saved.PasswordHash, "OriginalPassword1"));
        Assert.Null(saved.AuthenticationStamp);
    }

    [Fact]
    public async Task LoginHashUpgradeCannotOverwriteAConcurrentPasswordReset()
    {
        var newStamp = Guid.NewGuid(); var newHash = _hasher.HashPassword(_user, "ResetPassword123");
        var racingHasher = new RacingHasher(_hasher, () => _db.Users.Where(user => user.Id == _user.Id)
            .ExecuteUpdate(setters => setters.SetProperty(user => user.AuthenticationStamp, newStamp)
                .SetProperty(user => user.PasswordHash, newHash)));
        var service = new AuthenticationService(_db, racingHasher, new AccessTokens());
        var login = await service.LoginAsync(new("reset@example.test", "OriginalPassword1"), CancellationToken.None);
        Assert.Null(login);
        var saved = await _db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(newStamp, saved.AuthenticationStamp); Assert.Equal(newHash, saved.PasswordHash);
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
    private sealed class Sender : IPasswordResetEmailSender
    {
        public List<(string Email, string Token, string Language)> Messages { get; } = [];
        public Task SendAsync(string email, string token, string language, CancellationToken cancellationToken)
        { Messages.Add((email, token, language)); return Task.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class AccessTokens : IAccessTokenService
    {
        public (string Token, DateTimeOffset ExpiresAtUtc) Create(AuthenticatedUser user) => ("mock-token", DateTimeOffset.UtcNow.AddMinutes(1));
    }
    private sealed class RacingHasher(PasswordHasher<UserAccount> hasher, Action race) : IPasswordHasher<UserAccount>
    {
        public string HashPassword(UserAccount user, string password) => hasher.HashPassword(user, password);
        public PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword)
        { race(); return PasswordVerificationResult.SuccessRehashNeeded; }
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
