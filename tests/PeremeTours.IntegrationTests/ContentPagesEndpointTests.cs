using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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

public sealed class ContentPagesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FakePages _pages = new();
    private readonly HttpClient _client;

    public ContentPagesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-content-page-policy-test-signing-key-2026");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IContentPageService>(_pages);
                services.AddScoped<ITourContentService, FailingTours>();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            });
        }).CreateClient();
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Service", HttpStatusCode.Forbidden)]
    [InlineData("Editor", HttpStatusCode.OK)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task OnlyContentManagersCanManageContent(string? role, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/content-pages?kind=blog");
        if (role is not null) request.Headers.Add("X-Test-Role", role);
        using var response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("missing-title")]
    [InlineData("missing-body")]
    [InlineData("external-cover")]
    [InlineData("javascript-cover")]
    [InlineData("english-without-body")]
    [InlineData("long-meta-description")]
    public async Task InvalidContentIsRejectedBeforeSaving(string mode)
    {
        var node = JsonSerializer.SerializeToNode(Request(), JsonOptions)!;
        switch (mode)
        {
            case "kind": node["kind"] = "news"; break;
            case "missing-title": node["tr"]!["title"] = " "; break;
            case "missing-body": node["tr"]!["body"] = ""; break;
            case "external-cover": node["coverImageUrl"] = "https://tracker.example/a.png"; break;
            case "javascript-cover": node["coverImageUrl"] = "javascript:alert(1)"; break;
            case "english-without-body": node["en"] = new JsonObject { ["title"] = "English title" }; break;
            case "long-meta-description": node["tr"]!["metaDescription"] = new string('x', 201); break;
        }
        using var response = await Send(HttpMethod.Post, "/api/v1/admin/content-pages", node);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _pages.Saves);
    }

    [Theory]
    [InlineData("conflict", HttpStatusCode.Conflict)]
    [InlineData("reserved", HttpStatusCode.BadRequest)]
    [InlineData("ok", HttpStatusCode.Created)]
    public async Task SlugProblemsAreReportedClearly(string mode, HttpStatusCode expected)
    {
        _pages.Mode = mode;
        using var response = await Send(HttpMethod.Post, "/api/v1/admin/content-pages", JsonSerializer.SerializeToNode(Request(), JsonOptions)!);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task PublicEndpointsAreAnonymousAndValidateInput()
    {
        using var post = await _client.GetAsync("/api/v1/blog/bogaz-turu?language=tr");
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("no-store", post.Headers.CacheControl!.ToString());
        using var missing = await _client.GetAsync("/api/v1/pages/yok?language=tr");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var badLanguage = await _client.GetAsync("/api/v1/blog?language=de");
        Assert.Equal(HttpStatusCode.BadRequest, badLanguage.StatusCode);
        using var badPage = await _client.GetAsync("/api/v1/blog?pageSize=500");
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    [Fact]
    public async Task SitemapListsPagesWithLanguageAlternatesEvenWhenToursAreUnavailable()
    {
        using var response = await _client.GetAsync("/sitemap.xml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var locations = xml.Descendants(ns + "loc").Select(item => item.Value).ToList();
        Assert.Contains("https://pereme.com.tr/", locations);
        Assert.Contains("https://pereme.com.tr/blog/bogaz-turu", locations);
        Assert.Contains("https://pereme.com.tr/en/blog/bosphorus-cruise", locations);
        Assert.Contains("https://pereme.com.tr/hakkimizda", locations);
        Assert.DoesNotContain(locations, item => item.Contains("/tur/", StringComparison.Ordinal));
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        Assert.Equal(4, xml.Descendants(xhtml + "link").Count());
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, JsonNode body)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Test-Role", "Admin");
        return await _client.SendAsync(request);
    }

    private static object Request() => new
    {
        kind = "blog", isPublished = true, coverImageUrl = "/assets/tour-sunset.webp", showInFooter = false, sortOrder = 0,
        tr = new { title = "Boğaz Turu", slug = (string?)null, summary = "Özet", body = "<p>İçerik</p>", metaTitle = (string?)null, metaDescription = (string?)null },
    };

    private sealed class FakePages : IContentPageService
    {
        public string Mode { get; set; } = "ok";
        public int Saves { get; private set; }
        private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

        public Task<PublicContentPageList> ListPublishedAsync(string kind, string language, int page, int pageSize, CancellationToken cancellationToken)
            => Task.FromResult(new PublicContentPageList([], 0, page, pageSize));
        public Task<PublicContentPage?> GetPublishedAsync(string kind, string language, string slug, CancellationToken cancellationToken)
            => Task.FromResult<PublicContentPage?>(slug == "bogaz-turu"
                ? new(kind, language, "Boğaz Turu", slug, null, "<p>İçerik</p>", null, null, null, Now, Now, slug, null) : null);
        public Task<IReadOnlyList<ContentPageLink>> ListFooterPagesAsync(string language, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ContentPageLink>>([]);
        public Task<IReadOnlyList<ContentPageSitemapEntry>> ListSitemapEntriesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ContentPageSitemapEntry>>([
                new(ContentPageKinds.Blog, "bogaz-turu", "bosphorus-cruise", Now), new(ContentPageKinds.Page, "hakkimizda", null, Now)]);
        public Task<IReadOnlyList<ContentPageSummary>> ListAdminAsync(string kind, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ContentPageSummary>>([]);
        public Task<ContentPageSummary> CreateAsync(SaveContentPageCommand command, CancellationToken cancellationToken)
        {
            Saves++;
            return Mode switch
            {
                "conflict" => throw new ContentPageSlugConflictException("tr"),
                "reserved" => throw new ContentPageReservedSlugException("iletisim"),
                _ => Task.FromResult(new ContentPageSummary(Guid.NewGuid(), command.Kind, command.IsPublished, Now, null, false, 0,
                    new(command.Tr.Title, "bogaz-turu", null, command.Tr.Body, null, null), null, Now, Now)),
            };
        }
        public Task<ContentPageSummary?> UpdateAsync(Guid id, SaveContentPageCommand command, CancellationToken cancellationToken)
            => Task.FromResult<ContentPageSummary?>(null);
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FailingTours : ITourContentService
    {
        public Task<IReadOnlyList<PublicTourItem>> ListPublicAsync(CancellationToken cancellationToken) => throw new HttpRequestException("catalog down");
        public Task<IReadOnlyList<AdminTourContentItem>> ListAdminAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdminTourContentItem?> UpdateAsync(int externalTourId, UpdateTourContentCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdminTourContentItem?> UploadImageAsync(int externalTourId, Stream content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteImageAsync(int externalTourId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TourImageFile?> GetImageAsync(int externalTourId, CancellationToken cancellationToken) => throw new NotSupportedException();
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
