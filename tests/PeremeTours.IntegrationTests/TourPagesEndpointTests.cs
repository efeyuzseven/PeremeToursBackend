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
    [InlineData("unknown-type")]
    [InlineData("long-excluded-item")]
    [InlineData("too-many-excluded-items")]
    [InlineData("too-many-booking-notes")]
    [InlineData("null-booking-note")]
    [InlineData("booking-note-without-title")]
    [InlineData("long-booking-note")]
    [InlineData("long-details")]
    [InlineData("map-without-url")]
    [InlineData("map-other-host")]
    [InlineData("map-http")]
    [InlineData("map-not-embed")]
    [InlineData("map-javascript")]
    [InlineData("map-url-on-text-section")]
    [InlineData("map-unknown-id")]
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
            case "unknown-type": node["sections"]![0]!["type"] = "html"; break;
            case "long-excluded-item": node["sections"]![0]!["en"]!["excludedItems"] = new JsonArray(new string('x', 401)); break;
            case "too-many-excluded-items": node["sections"]![0]!["tr"]!["excludedItems"] = new JsonArray(Enumerable.Range(0, 13).Select(_ => JsonValue.Create("x")).ToArray()); break;
            case "too-many-booking-notes": node["tr"]!["bookingNotes"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode)new JsonObject { ["title"] = "Not" }).ToArray()); break;
            case "null-booking-note": node["tr"]!["bookingNotes"] = new JsonArray((JsonNode?)null); break;
            case "booking-note-without-title": node["en"]!["bookingNotes"] = new JsonArray(new JsonObject { ["text"] = "Text" }); break;
            case "long-details": node["tr"]!["details"] = new string('x', 50001); break;
            case "map-without-url": node["sections"]![0]!["type"] = "map"; node["sections"]![0]!["id"] = "route"; break;
            case "map-other-host": node["sections"]![0]!["type"] = "map"; node["sections"]![0]!["id"] = "route"; node["sections"]![0]!["mapUrl"] = "https://evil.example/maps/embed?pb=1"; break;
            case "map-http": node["sections"]![0]!["type"] = "map"; node["sections"]![0]!["id"] = "route"; node["sections"]![0]!["mapUrl"] = "http://www.google.com/maps/embed?pb=1"; break;
            case "map-not-embed": node["sections"]![0]!["type"] = "map"; node["sections"]![0]!["id"] = "route"; node["sections"]![0]!["mapUrl"] = "https://www.google.com/search?q=x"; break;
            case "map-javascript": node["sections"]![0]!["type"] = "map"; node["sections"]![0]!["id"] = "route"; node["sections"]![0]!["mapUrl"] = "javascript:alert(1)"; break;
            case "map-unknown-id": node["sections"]![0]!["type"] = "map"; node["sections"]![0]!["mapUrl"] = "https://www.google.com/maps/embed?pb=1"; break;
            case "map-url-on-text-section": node["sections"]![0]!["mapUrl"] = "https://www.google.com/maps/embed?pb=1"; break;
            case "long-booking-note": node["tr"]!["bookingNotes"] = new JsonArray(new JsonObject { ["title"] = "Not", ["text"] = new string('x', 301) }); break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/tour-pages/sunset") { Content = JsonContent.Create(node) };
        request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IncludedSectionsAndBookingNotesAreSavedAndOptional()
    {
        const string DetailsText = "## Dahil olanlar\n+ Akşam yemeği\nx Bahşiş";
        var document = Document() with
        {
            Tr = Document().Tr with { BookingNotes = [new("Ücretsiz iptal", "24 saat öncesine kadar"), new("Anında onay", null)],
                Details = DetailsText },
            En = Document().En with { BookingNotes = [] },
            Sections = [new("included", "included", "white", true, [], new("Dahil olanlar", null, ["Akşam yemeği"], ["Ekstra içecekler"]),
                new("What's included", null, ["Dinner"]))],
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/tour-pages/sunset") { Content = JsonContent.Create(document) };
        request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<TourPageItem>())!.Document;
        Assert.Equal(["Ücretsiz iptal", "Anında onay"], saved.Tr.BookingNotes!.Select(note => note.Title));
        Assert.Empty(saved.En.BookingNotes!);
        Assert.Equal(DetailsText, saved.Tr.Details);
        Assert.Null(saved.En.Details);
        var section = Assert.Single(saved.Sections);
        Assert.Equal(["Ekstra içecekler"], section.Tr.ExcludedItems!);
        Assert.Null(section.En.ExcludedItems);
    }

    [Fact]
    public async Task MapSectionsWithGoogleEmbedUrlsAreSaved()
    {
        const string MapUrl = "https://www.google.com/maps/embed?pb=!1m18!1m12";
        var document = Document() with
        {
            Sections = [new("meeting-point", "map", "white", true, [], new("Buluşma noktası", "Kabataş iskelesi", []), new("Meeting point", "Kabatas pier", []), MapUrl),
                new("route", "map", "white", true, [], new("Güzergah", null, []), new("Route", null, []), MapUrl),
                new("service-point", "map", "white", false, [], new("Servis noktası", null, []), new("Shuttle point", null, []), MapUrl)],
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/tour-pages/sunset") { Content = JsonContent.Create(document) };
        request.Headers.Add("X-Test-Role", "Admin");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sections = (await response.Content.ReadFromJsonAsync<TourPageItem>())!.Document.Sections;
        Assert.Equal(["meeting-point", "route", "service-point"], sections.Select(section => section.Id));
        Assert.All(sections, section => Assert.Equal(MapUrl, section.MapUrl));
        Assert.Equal("Kabataş iskelesi", sections[0].Tr.Body);
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
