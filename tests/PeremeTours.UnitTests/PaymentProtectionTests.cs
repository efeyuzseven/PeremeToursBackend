using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Payments;
using PeremeTours.Infrastructure.Payments;

namespace PeremeTours.UnitTests;

public sealed class PaymentProtectionTests
{
    [Fact]
    public void SameEmailIsLimitedEvenWithDifferentCards()
    {
        var guard = Guard(new Clock());
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(PaymentAttemptLimit.None, guard.TryAcquire("Guest@Example.com", $"411111111111111{attempt}", out _));

        Assert.Equal(PaymentAttemptLimit.Email, guard.TryAcquire(" guest@example.com ", "5555444433332222", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void SameCardIsLimitedEvenWithDifferentEmails()
    {
        var guard = Guard(new Clock());
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(PaymentAttemptLimit.None, guard.TryAcquire($"guest{attempt}@example.com", "4111 1111 1111 1111", out _));

        Assert.Equal(PaymentAttemptLimit.Card, guard.TryAcquire("other@example.com", "4111111111111111", out _));
    }

    [Fact]
    public void IdentityLimitResetsAfterWindow()
    {
        var clock = new Clock();
        var guard = Guard(clock);
        for (var attempt = 0; attempt < 3; attempt++) guard.TryAcquire("guest@example.com", "4111111111111111", out _);

        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(PaymentAttemptLimit.None, guard.TryAcquire("guest@example.com", "4111111111111111", out _));
    }

    [Fact]
    public void SiteWideLimitStopsRotatingIdentities()
    {
        var guard = Guard(new Clock(), paymentsPerHour: 5);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(PaymentAttemptLimit.None, guard.TryAcquire($"guest{attempt}@example.com", $"411111111111{attempt:D4}", out _));

        Assert.Equal(PaymentAttemptLimit.SiteWide, guard.TryAcquire("fresh@example.com", "5555444433332222", out _));
    }

    [Fact]
    public async Task DisabledRecaptchaAcceptsRequestsWithoutToken()
    {
        var verifier = Verifier(new PaymentProtectionOptions(), _ => throw new InvalidOperationException("must not call Google"));
        Assert.True(await verifier.VerifyAsync(null, "203.0.113.10", CancellationToken.None));
    }

    [Fact]
    public async Task EnabledRecaptchaRequiresSecret()
    {
        var verifier = Verifier(new PaymentProtectionOptions { RecaptchaEnabled = true }, _ => Json("""{"success":true}"""));
        await Assert.ThrowsAsync<PaymentConfigurationException>(() => verifier.VerifyAsync("token", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"success":true}""", true)]
    [InlineData("""{"success":false,"error-codes":["timeout-or-duplicate"]}""", false)]
    public async Task EnabledRecaptchaUsesGoogleVerdict(string body, bool expected)
    {
        string? sentForm = null;
        var verifier = Verifier(Enabled(), request =>
        {
            sentForm = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(body);
        });

        Assert.Equal(expected, await verifier.VerifyAsync("client-token", "203.0.113.10", CancellationToken.None));
        Assert.Contains("response=client-token", sentForm);
        Assert.Contains("remoteip=203.0.113.10", sentForm);
    }

    [Fact]
    public async Task EnabledRecaptchaRejectsMissingTokenAndGoogleOutages()
    {
        Assert.False(await Verifier(Enabled(), _ => Json("""{"success":true}""")).VerifyAsync(" ", null, CancellationToken.None));
        Assert.False(await Verifier(Enabled(), _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            .VerifyAsync("client-token", null, CancellationToken.None));
    }

    private static PaymentAttemptGuard Guard(TimeProvider clock, int paymentsPerHour = 40) =>
        new(Options.Create(new PaymentProtectionOptions { PaymentsPerHour = paymentsPerHour }), clock);

    private static PaymentProtectionOptions Enabled() => new() { RecaptchaEnabled = true, RecaptchaSecretKey = "fixture-secret" };

    private static RecaptchaVerifier Verifier(PaymentProtectionOptions options, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)), Options.Create(options), NullLogger<RecaptchaVerifier>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
