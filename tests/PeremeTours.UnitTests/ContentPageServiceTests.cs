using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PeremeTours.Application.Content;
using PeremeTours.Infrastructure.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.UnitTests;

public sealed class ContentPageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly FixedClock _clock = new();

    public ContentPageServiceTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, CancellationEmailTests.TestModel>().Options);
        _db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData("Boğaz'da Gün Batımı: İstanbul Rehberi!", "bogazda-gun-batimi-istanbul-rehberi")]
    [InlineData("  ÇOK   Şık  Öğle Yemeği  ", "cok-sik-ogle-yemegi")]
    [InlineData("Kız Kulesi & Üsküdar — 2026", "kiz-kulesi-uskudar-2026")]
    [InlineData("Café crème", "cafe-creme")]
    [InlineData("!!!", "")]
    public void SlugsAreReadableAscii(string input, string expected) => Assert.Equal(expected, ContentSlug.Create(input));

    [Fact]
    public async Task SlugIsDerivedFromTitleAndMustBeUniquePerKindAndLanguage()
    {
        var first = await Service().CreateAsync(Command(ContentPageKinds.Blog, "Boğaz Turu Rehberi"), CancellationToken.None);
        Assert.Equal("bogaz-turu-rehberi", first.Tr.Slug);
        var conflict = await Assert.ThrowsAsync<ContentPageSlugConflictException>(() =>
            Service().CreateAsync(Command(ContentPageKinds.Blog, "Boğaz turu rehberi"), CancellationToken.None));
        Assert.Equal("tr", conflict.Language);
        // A page may reuse a blog post's slug: they live under different addresses.
        var page = await Service().CreateAsync(Command(ContentPageKinds.Page, "Boğaz Turu Rehberi"), CancellationToken.None);
        Assert.Equal("bogaz-turu-rehberi", page.Tr.Slug);
        // Saving an item again keeps its own slug without a conflict.
        var updated = await Service().UpdateAsync(first.Id, Command(ContentPageKinds.Blog, "Boğaz Turu Rehberi", body: "<p>Yeni</p>"), CancellationToken.None);
        Assert.Equal("bogaz-turu-rehberi", updated!.Tr.Slug);
    }

    [Fact]
    public async Task PagesCannotTakeAddressesTheSiteAlreadyUses()
    {
        var reserved = await Assert.ThrowsAsync<ContentPageReservedSlugException>(() =>
            Service().CreateAsync(Command(ContentPageKinds.Page, "İletişim"), CancellationToken.None));
        Assert.Equal("iletisim", reserved.Slug);
        var blog = await Service().CreateAsync(Command(ContentPageKinds.Blog, "İletişim"), CancellationToken.None);
        Assert.Equal("iletisim", blog.Tr.Slug);
    }

    [Fact]
    public async Task OnlyPublishedAndDueItemsAreVisibleAndEnglishNeedsAFullTranslation()
    {
        await Service().CreateAsync(Command(ContentPageKinds.Blog, "Taslak", published: false), CancellationToken.None);
        await Service().CreateAsync(Command(ContentPageKinds.Blog, "Gelecek", publishedAt: _clock.GetUtcNow().AddDays(2)), CancellationToken.None);
        await Service().CreateAsync(Command(ContentPageKinds.Blog, "Yayında", en: new("Live post", null, "Summary", "<p>Body</p>", null, null)), CancellationToken.None);
        await Service().CreateAsync(Command(ContentPageKinds.Blog, "Sadece Türkçe"), CancellationToken.None);

        var tr = await Service().ListPublishedAsync(ContentPageKinds.Blog, "tr", 1, 12, CancellationToken.None);
        Assert.Equal(["Sadece Türkçe", "Yayında"], tr.Items.Select(item => item.Title).Order(StringComparer.Ordinal));
        Assert.Equal(2, tr.Total);
        var en = await Service().ListPublishedAsync(ContentPageKinds.Blog, "en", 1, 12, CancellationToken.None);
        Assert.Equal("live-post", Assert.Single(en.Items).Slug);

        var detail = await Service().GetPublishedAsync(ContentPageKinds.Blog, "en", "live-post", CancellationToken.None);
        Assert.Equal("yayinda", detail!.SlugTr);
        Assert.Null(await Service().GetPublishedAsync(ContentPageKinds.Blog, "tr", "taslak", CancellationToken.None));
        Assert.Null(await Service().GetPublishedAsync(ContentPageKinds.Blog, "tr", "gelecek", CancellationToken.None));

        _clock.Advance(TimeSpan.FromDays(3));
        Assert.NotNull(await Service().GetPublishedAsync(ContentPageKinds.Blog, "tr", "gelecek", CancellationToken.None));
    }

    [Fact]
    public async Task PublishDateIsSetOnFirstPublishAndKeptOnLaterEdits()
    {
        var draft = await Service().CreateAsync(Command(ContentPageKinds.Blog, "Taslak", published: false), CancellationToken.None);
        Assert.Null(draft.PublishedAtUtc);
        var published = await Service().UpdateAsync(draft.Id, Command(ContentPageKinds.Blog, "Taslak"), CancellationToken.None);
        var firstDate = published!.PublishedAtUtc;
        Assert.Equal(_clock.GetUtcNow(), firstDate);
        _clock.Advance(TimeSpan.FromDays(1));
        var edited = await Service().UpdateAsync(draft.Id, Command(ContentPageKinds.Blog, "Taslak", body: "<p>Düzeltme</p>"), CancellationToken.None);
        Assert.Equal(firstDate, edited!.PublishedAtUtc);
    }

    [Fact]
    public async Task FooterListsOnlyPublishedPagesMarkedForTheFooterInOrder()
    {
        await Service().CreateAsync(Command(ContentPageKinds.Page, "Hakkımızda", footer: true, sortOrder: 2), CancellationToken.None);
        await Service().CreateAsync(Command(ContentPageKinds.Page, "Gizlilik", footer: true, sortOrder: 1), CancellationToken.None);
        await Service().CreateAsync(Command(ContentPageKinds.Page, "Kampanya", footer: false), CancellationToken.None);
        await Service().CreateAsync(Command(ContentPageKinds.Blog, "Blog yazısı", footer: true), CancellationToken.None);

        var footer = await Service().ListFooterPagesAsync("tr", CancellationToken.None);
        Assert.Equal(["gizlilik", "hakkimizda"], footer.Select(item => item.Slug));
        Assert.Equal(4, (await Service().ListSitemapEntriesAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task DeletedItemsDisappear()
    {
        var item = await Service().CreateAsync(Command(ContentPageKinds.Page, "Geçici"), CancellationToken.None);
        Assert.True(await Service().DeleteAsync(item.Id, CancellationToken.None));
        Assert.False(await Service().DeleteAsync(item.Id, CancellationToken.None));
        Assert.Null(await Service().GetPublishedAsync(ContentPageKinds.Page, "tr", "gecici", CancellationToken.None));
    }

    private ContentPageService Service() { _db.ChangeTracker.Clear(); return new(_db, _clock); }

    private static SaveContentPageCommand Command(string kind, string title, bool published = true, DateTimeOffset? publishedAt = null,
        string body = "<p>İçerik</p>", SaveContentPageText? en = null, bool footer = false, int sortOrder = 0) =>
        new(kind, published, publishedAt, null, footer, sortOrder, new(title, null, "Özet", body, null, null), en);

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
