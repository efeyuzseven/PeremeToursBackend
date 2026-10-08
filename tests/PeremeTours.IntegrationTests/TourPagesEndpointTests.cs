using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;

namespace PeremeTours.IntegrationTests;

public sealed class TourPagesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public TourPagesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-tour-page-policy-test-signing-key-2026-secure");
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<ITourPageService, FakePages>();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            });
        }).CreateClient();
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task ReadingAndWritingTheEditorAreAdminOnly(string? role, HttpStatusCode expected)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Get ? "/api/v1/admin/tour-pages" : "/api/v1/admin/tour-pages/sunset");
            if (role is not null) request.Headers.Add("X-Test-Role", role);
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(Document());
            using var response = await _client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Fact]
    public async Task PublicPageIsAnonymousButUnknownKeysReturnNotFound()
    {
        using var response = await _client.GetAsync("/api/v1/tour-pages/sunset");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var published = await response.Content.ReadFromJsonAsync<TourPageItem>();
        Assert.Single(published!.Document.Sections);
        Assert.DoesNotContain(published.Document.Sections, section => section.Id == "hidden");
        using var missing = await _client.GetAsync("/api/v1/tour-pages/unknown");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("layout")]
    [InlineData("javascript")]
    [InlineData("protocol-relative")]
    [InlineData("duplicate")]
    [InlineData("null-section")]
    [InlineData("null-language")]
    [InlineData("null-items")]
    [InlineData("too-many-sections")]
    [InlineData("too-many-images")]
    [InlineData("long-item")]
    public async Task InvalidPageDocumentsReturnValidationErrors(string invalid)
    {
        var node = JsonSerializer.SerializeToNode(Document(), JsonOptions)!;
        switch (invalid)
        {
            case "layout": node["heroLayout"] = "arbitrary-css"; break;
            case "javascript": node["heroImageUrl"] = "javascript:alert(1)"; break;
            case "protocol-relative": node["heroImageUrl"] = "//untrusted.example/image.png"; break;
            case "duplicate": node["sections"]!.AsArray().Add(node["sections"]![0]!.DeepClone()); break;
            case "null-section": node["sections"]!.AsArray().Add(null); break;
            case "null-language": node["en"] = null; break;
            case "null-items": node["sections"]![0]!["tr"]!["items"] = null; break;
            case "too-many-sections": for (var i = 0; i < 8; i++) node["sections"]!.AsArray().Add(node["sections"]![0]!.DeepClone()); break;
            case "too-many-images": node["sections"]![0]!["imageUrls"] = new JsonArray(Enumerable.Range(0, 7).Select(_ => JsonValue.Create("/assets/test.webp")).ToArray()); break;
            case "long-item": node["sections"]![0]!["tr"]!["items"] = new JsonArray(new string('x', 401)); break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/tour-pages/sunset") { Content = JsonContent.Create(node) };
        request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImageUploadsRejectSpoofedImageSignatures()
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent("not an image"u8.ToArray());
        file.Headers.ContentType = new("image/png");
        content.Add(file, "file", "test.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/tour-pages/sunset/images") { Content = content };
        request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static TourPageDocument Document() => new("cover", null,
        new("Label", "Türkçe başlık", "Türkçe açıklama", "Rezervasyon yap", "Turlar", "Türkçe SEO"),
        new("Label", "English title", "English description", "Book now", "Tours", "English SEO"),
        [new("experience", "text", "white", true, [], new("Başlık", "Metin", []), new("Title", "Text", [])),
            new("hidden", "text", "white", false, [], new("Taslak", "Gizli metin", []), new("Draft", "Hidden text", []))]);

    private sealed class FakePages : ITourPageService
    {
        public Task<IReadOnlyList<TourPageItem>> ListAdminAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TourPageItem>>([Item()]);
        public Task<TourPageItem?> GetAsync(string key, bool includeHidden, CancellationToken cancellationToken)
            => Task.FromResult<TourPageItem?>(key == "sunset" ? Item() : null);
        public Task<TourPageItem?> UpdateAsync(string key, TourPageDocument document, CancellationToken cancellationToken)
            => Task.FromResult<TourPageItem?>(Item() with { Document = document });
        public Task<TourPageImageResult?> UploadImageAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Invalid image must not reach storage.");
        public Task<TourImageFile?> GetImageAsync(string key, Guid imageId, CancellationToken cancellationToken) => Task.FromResult<TourImageFile?>(null);
        private static TourPageItem Item() => new("sunset", "sunset", null, "sunset", Document(), false, null);
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
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
