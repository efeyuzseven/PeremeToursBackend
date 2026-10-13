using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;

namespace PeremeTours.IntegrationTests;

public sealed class HomepageAndContactEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Categories = ["turkish-night", "sunset", "daytime", "bosphorus"];
    private readonly HttpClient _client;
    private readonly ContactService _contact = new();
    private readonly Images _images = new();
    public HomepageAndContactEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PeremeToursDatabase", "Host=localhost;Database=tests;Username=postgres;Password=postgres");
            builder.UseSetting("Jwt:Key", "pereme-homepage-contact-test-signing-key-secure");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IHomepageContentService, ContentService>(); services.AddSingleton<IHomepageImageService>(_images);
                services.AddSingleton<IContactRequestService>(_contact);
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            });
        }).CreateClient();
    }
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task OnlyAdminsCanSaveHomepageAndUploadImages(string? role, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/site-content/homepage") { Content = JsonContent.Create(Document()) };
        if (role is not null) request.Headers.Add("X-Test-Role", role);
        using var response = await _client.SendAsync(request); Assert.Equal(expected, response.StatusCode);
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/site-content/homepage/images") { Content = Upload(true) };
        if (role is not null) upload.Headers.Add("X-Test-Role", role);
        using var uploaded = await _client.SendAsync(upload); Assert.Equal(expected, uploaded.StatusCode);
    }
    [Fact]
    public async Task PublicContentIsNotStaleAndImagesAreImmutable()
    {
        using var response = await _client.GetAsync("/api/v1/site-content/homepage");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var image = await _client.GetAsync($"/api/v1/site-content/homepage/images/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode); Assert.Contains("immutable", image.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(image.Headers.GetValues("X-Content-Type-Options")));
    }
    [Theory]
    [InlineData("null-language")][InlineData("null-benefit")][InlineData("null-services")][InlineData("null-instagram")]
    [InlineData("external-image")][InlineData("javascript-image")][InlineData("traversal")][InlineData("invalid-focus")]
    [InlineData("long-text")][InlineData("invalid-section")][InlineData("long-extra")][InlineData("null-extra")]
    public async Task UnsafeOrIncompleteDocumentsFailValidation(string mode)
    {
        var node = JsonSerializer.SerializeToNode(Document(), JsonOptions)!;
        switch (mode)
        {
            case "null-language": node["tr"] = null; break;
            case "null-benefit": node["tr"]!["why"]!["benefits"]![0] = null; break;
            case "null-services": node["tr"]!["services"]!["items"] = null; break;
            case "null-instagram": node["instagramUrls"] = null; break;
            case "external-image": node["images"] = new JsonObject { ["hero"] = "https://tracker.example/image.png" }; break;
            case "javascript-image": node["images"] = new JsonObject { ["hero"] = "javascript:alert(1)" }; break;
            case "traversal": node["images"] = new JsonObject { ["hero"] = "/assets/../secret.png" }; break;
            case "invalid-focus": node["imagePositions"] = new JsonObject { ["hero"] = "url(tracker)" }; break;
            case "long-text": node["tr"]!["hero"]!["lead"] = new string('x', 3001); break;
            case "invalid-section": node["hiddenSections"] = new JsonArray("payments"); break;
            case "long-extra": node["tr"]!["texts"] = new JsonObject { ["hero.eyebrow"] = new string('x', 3001) }; break;
            case "null-extra": node["tr"]!["texts"] = new JsonObject { ["hero.eyebrow"] = null }; break;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/site-content/homepage") { Content = JsonContent.Create(node) };
        request.Headers.Add("X-Test-Role", "Admin"); using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task SpoofedImageNeverReachesStorage()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/site-content/homepage/images") { Content = Upload(false) };
        request.Headers.Add("X-Test-Role", "Admin"); using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, _images.Uploads);
    }
    [Fact]
    public async Task ContactRequestsAreAnonymousAndLimitedAndHoneyPotDoesNotSendMail()
    {
        var request = Submission();
        using var honey = await _client.PostAsJsonAsync("/api/v1/contact-requests", request with { Website = "bot" });
        Assert.Equal(HttpStatusCode.Accepted, honey.StatusCode); Assert.Equal(0, _contact.Calls);
        for (var i = 0; i < 4; i++)
        {
            using var response = await _client.PostAsJsonAsync("/api/v1/contact-requests", request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        using var limited = await _client.PostAsJsonAsync("/api/v1/contact-requests", request);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.Equal(4, _contact.Calls);
    }
    [Theory]
    [InlineData("email")][InlineData("blank")][InlineData("header")][InlineData("language")][InlineData("id")][InlineData("message")]
    public async Task ContactValidationRejectsInvalidFieldsBeforePersistence(string mode)
    {
        var request = Submission();
        request = mode switch { "email" => request with { Email = "invalid" }, "blank" => request with { Name = "  " },
            "header" => request with { Topic = "Subject\r\nBcc: test@example.test" }, "language" => request with { Language = "xx" },
            "id" => request with { RequestId = Guid.Empty }, _ => request with { Message = new string('x', 3001) } };
        using var response = await _client.PostAsJsonAsync("/api/v1/contact-requests", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, _contact.Calls);
    }
    private static MultipartFormDataContent Upload(bool valid)
    {
        var data = new MultipartFormDataContent();
        var file = new ByteArrayContent(valid ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZfkAAAAASUVORK5CYII=") : "not an image"u8.ToArray());
        file.Headers.ContentType = new("image/png"); data.Add(file, "file", "fixture.png"); return data;
    }
    private static ContactRequestSubmission Submission() => new(Guid.NewGuid(), "Fixture", "fixture@example.test", "Subject", "Message", "en");
    private static HomepageContentDocument Document()
    {
        var language = new HomepageLanguageContent(new("Heading", "Accent", "Description"), new("Label", "Heading", "Accent", "Description"),
            new("Label", "Heading", "Accent", "Description", Categories.Select(key => new HomepageServiceItem(key, key, "Text", "View", "Book")).ToArray()),
            new("Label", "Heading", "Accent", "Reviews", Enumerable.Range(0, 4).Select(_ => new HomepageBenefitItem("Title", "Description")).ToArray()),
            new("Label", "Heading", "Accent", "Description", "Quote", "Details"), new("Pre", "Title", "Book"));
        return new(language, language, []);
    }
    private sealed class ContentService : IHomepageContentService
    {
        public Task<HomepageContentDocument> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Document());
        public Task<HomepageContentDocument> UpdateAsync(HomepageContentDocument document, CancellationToken cancellationToken) => Task.FromResult(document);
    }
    private sealed class Images : IHomepageImageService
    {
        public int Uploads { get; private set; }
        public Task<TourPageImageResult> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken) { Uploads++; return Task.FromResult(new TourPageImageResult($"/api/v1/site-content/homepage/images/{Guid.NewGuid()}")); }
        public Task<TourImageFile?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<TourImageFile?>(new([1, 2, 3], "image/png"));
    }
    private sealed class ContactService : IContactRequestService
    {
        public int Calls { get; private set; }
        public Task<ContactRequestReceipt> SubmitAsync(ContactRequestSubmission request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new ContactRequestReceipt(request.RequestId)); }
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test")), "Test")));
        }
    }
}
