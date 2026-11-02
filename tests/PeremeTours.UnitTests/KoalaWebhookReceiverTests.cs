using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Infrastructure.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.UnitTests;

public sealed class KoalaWebhookReceiverTests : IDisposable
{
    private const string Secret = "test-signing-secret";
    private const string Html = """
        <h1>Boğaz Turu Rehberi: <em>2026</em></h1>
        <p><strong>İstanbul Boğazı’nı gün batımında görmek için</strong> en iyi saatleri anlattık.</p><img src="https://koala.sh/api/image/v2-abc.jpg?width=1536&amp;height=1024" alt="Boğaz" width="1536" height="1024">
        <h2>Ne zaman gidilmeli?</h2>
        <p>Akşamüstü.</p>
        """;
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3];

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly Images _images = new();
    private readonly ImageServer _server = new();

    public KoalaWebhookReceiverTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, CancellationEmailTests.TestModel>().Options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task DeliveriesNeedAConfiguredSecretAndAValidSignature()
    {
        var body = Payload("a1", Html);
        Assert.Equal(KoalaWebhookStatus.NotConfigured, (await Receiver(secret: "").ReceiveAsync(body, Sign(body), null, null, default)).Status);
        Assert.Equal(KoalaWebhookStatus.BadSignature, (await Receiver().ReceiveAsync(body, null, null, null, default)).Status);
        Assert.Equal(KoalaWebhookStatus.BadSignature, (await Receiver().ReceiveAsync(body, "sha256=00ff", null, null, default)).Status);
        Assert.Equal(KoalaWebhookStatus.BadSignature, (await Receiver().ReceiveAsync(body, Sign(Payload("a2", Html)), null, null, default)).Status);
        Assert.Empty(await _db.ContentPages.ToListAsync());

        var test = Encoding.UTF8.GetBytes("""{"event":"article.test","id":"test","output":{"html":"<h1>Test</h1>"}}""");
        Assert.Equal(KoalaWebhookStatus.Test, (await Receiver().ReceiveAsync(test, Sign(test), "article.test", null, default)).Status);
        Assert.Empty(await _db.ContentPages.ToListAsync());
    }

    [Fact]
    public async Task AnArticleBecomesADraftPostWithItsFirstPictureAsTheCover()
    {
        var body = Payload("a1", Html, new { targetKeyword = "boğaz turu", language = "Turkish" });
        var result = await Receiver().ReceiveAsync(body, Sign(body), "article.completed", "d1", default);

        Assert.Equal(KoalaWebhookStatus.Created, result.Status);
        var page = await _db.ContentPages.SingleAsync();
        Assert.Equal("koala:a1", page.ExternalId);
        Assert.Equal(ContentPageKinds.Blog, page.Kind);
        Assert.False(page.IsPublished);
        Assert.Null(page.PublishedAtUtc);
        Assert.Equal("Boğaz Turu Rehberi: 2026", page.TitleTr);
        Assert.Equal("bogaz-turu-rehberi-2026", page.SlugTr);
        Assert.DoesNotContain("<h1", page.BodyTr);
        Assert.DoesNotContain("<img", page.BodyTr);
        Assert.StartsWith("<p><strong>", page.BodyTr);
        Assert.Equal("İstanbul Boğazı’nı gün batımında görmek için en iyi saatleri anlattık.", page.SummaryTr);
        Assert.Equal(page.SummaryTr, page.MetaDescriptionTr);
        Assert.Equal("/api/v1/site-content/homepage/images/1", page.CoverImageUrl);
        Assert.Equal("https://koala.sh/api/image/v2-abc.jpg?width=1536&height=1024", _server.Requests.Single());
        Assert.Equal("image/jpeg", _images.ContentTypes.Single());
    }

    [Fact]
    public async Task AResendUpdatesTheSamePostAndKeepsItsAddressAndCover()
    {
        var first = Payload("a1", Html);
        await Receiver(publish: true).ReceiveAsync(first, Sign(first), null, null, default);
        var resend = Payload("a1", Html.Replace("Akşamüstü.", "Gün batımından bir saat önce."));
        var result = await Receiver().ReceiveAsync(resend, Sign(resend), null, null, default);

        Assert.Equal(KoalaWebhookStatus.Updated, result.Status);
        var page = await _db.ContentPages.SingleAsync();
        Assert.True(page.IsPublished);
        Assert.Contains("Gün batımından bir saat önce.", page.BodyTr);
        Assert.Equal("bogaz-turu-rehberi-2026", page.SlugTr);
        Assert.Single(_server.Requests);

        // Another article with the same title gets its own address.
        var other = Payload("b2", Html);
        Assert.Equal("bogaz-turu-rehberi-2026-2", (await Receiver().ReceiveAsync(other, Sign(other), null, null, default)).Slug);
    }

    [Fact]
    public async Task PayloadFieldsAreUsedWhenPresentAndUnknownImageHostsAreNotFetched()
    {
        var body = Payload("a1", Html.Replace("https://koala.sh/", "https://example.com/"),
            new { slug = "ozel-adres", metaDescription = "Kısa açıklama." });
        await Receiver().ReceiveAsync(body, Sign(body), null, null, default);

        var page = await _db.ContentPages.SingleAsync();
        Assert.Equal("ozel-adres", page.SlugTr);
        Assert.Equal("Kısa açıklama.", page.MetaDescriptionTr);
        Assert.Null(page.CoverImageUrl);
        Assert.Contains("https://example.com/api/image", page.BodyTr);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task EnglishArticlesAndPayloadsWithoutHtmlAreNotStored()
    {
        var english = Payload("a1", "<h1>Bosphorus guide</h1><p>Hi</p>", new { language = "English" });
        Assert.Equal(KoalaWebhookStatus.Skipped, (await Receiver().ReceiveAsync(english, Sign(english), null, null, default)).Status);
        var empty = Encoding.UTF8.GetBytes("""{"id":"a1","output":{}}""");
        Assert.Equal(KoalaWebhookStatus.InvalidPayload, (await Receiver().ReceiveAsync(empty, Sign(empty), null, null, default)).Status);
        Assert.Empty(await _db.ContentPages.ToListAsync());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private KoalaWebhookReceiver Receiver(string secret = Secret, bool publish = false) => new(_db, _images, new HttpClient(_server),
        Options.Create(new KoalaOptions { SigningSecret = secret, PublishImmediately = publish }), TimeProvider.System,
        NullLogger<KoalaWebhookReceiver>.Instance);

    private static byte[] Payload(string id, string html, object? config = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new { id, config = config ?? new { targetKeyword = "boğaz" }, output = new { html, polishedHtml = (string?)null } });

    private static string Sign(byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));

    private sealed class Images : IHomepageImageService
    {
        public List<string> ContentTypes { get; } = [];

        public Task<TourPageImageResult> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken)
        {
            ContentTypes.Add(contentType);
            return Task.FromResult(new TourPageImageResult($"/api/v1/site-content/homepage/images/{ContentTypes.Count}"));
        }

        public Task<TourImageFile?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ImageServer : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) });
        }
    }
}
