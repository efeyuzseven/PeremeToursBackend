using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Infrastructure.Content;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.UnitTests;

public sealed class TourPageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly FakeTours _tours = new();
    private readonly FakeImages _images = new();
    private readonly TourPageService _service;

    public TourPageServiceTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _service = new(_db, _tours, _images, _images);
    }

    [Fact]
    public async Task EveryServiceAndProviderTourHasAnEditablePageWithoutCreatingTours()
    {
        var result = await _service.ListAdminAsync(CancellationToken.None);
        Assert.Equal(6, result.Count);
        Assert.Equal(4, result.Count(page => page.ExternalTourId is null));
        Assert.Contains(result, page => page.Key == "tour-41");
        Assert.Contains(result, page => page.Key == "tour-42");
        Assert.All(result, page => { Assert.NotEmpty(page.Document.Tr.Title); Assert.NotEmpty(page.Document.En.Title); });
        Assert.Empty(await _db.TourPages.ToListAsync());
        Assert.Empty(await _db.TourTickets.ToListAsync());
    }

    [Fact]
    public async Task LayoutBothLanguagesAndSectionOrderPersistIndependentlyOfTourCardContent()
    {
        var original = (await _service.GetAsync("sunset", false, CancellationToken.None))!;
        var document = original.Document with { HeroLayout = "split", Tr = original.Document.Tr with { Title = "Özel Türkçe başlık" },
            En = original.Document.En with { Title = "Custom English title" },
            Sections = original.Document.Sections.Reverse().Select(section => section with { IsVisible = false }).ToArray() };
        await _service.UpdateAsync("sunset", document, CancellationToken.None);
        _db.ChangeTracker.Clear();
        var saved = (await _service.GetAsync("sunset", false, CancellationToken.None))!;
        Assert.Equal("split", saved.Document.HeroLayout);
        Assert.Equal("Özel Türkçe başlık", saved.Document.Tr.Title);
        Assert.Equal("Custom English title", saved.Document.En.Title);
        Assert.Equal("highlights", saved.Document.Sections[0].Id);
        Assert.All(saved.Document.Sections, section => Assert.False(section.IsVisible));
        Assert.True(saved.IsCustomized);
        Assert.NotNull(saved.UpdatedAtUtc);
        Assert.Empty(await _db.TourContents.ToListAsync());
        Assert.Empty(await _db.TourTickets.ToListAsync());
    }

    [Fact]
    public async Task HiddenProviderTourCannotBeExposedThroughItsDetailPage()
    {
        var editable = (await _service.GetAsync("tour-42", true, CancellationToken.None))!;
        Assert.Equal(42, editable.ExternalTourId);
        await _service.UpdateAsync("tour-42", editable.Document, CancellationToken.None);
        Assert.Null(await _service.GetAsync("tour-42", false, CancellationToken.None));
        Assert.NotNull(await _service.GetAsync("tour-41", false, CancellationToken.None));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("tour-0")]
    [InlineData("tour-041")]
    [InlineData("tour-999")]
    [InlineData("tour-2147483648")]
    [InlineData("../sunset")]
    public async Task UnknownPageKeysDoNotCreateOrExposePages(string key)
    {
        Assert.Null(await _service.GetAsync(key, false, CancellationToken.None));
        Assert.Null(await _service.UpdateAsync(key, TourPageDefaults.Create("sunset"), CancellationToken.None));
        Assert.Empty(await _db.TourPages.ToListAsync());
    }

    [Fact]
    public async Task UploadHasPageScopedIdentityAndDoesNotPublishAnUnsavedDraftImage()
    {
        await using var content = new MemoryStream([0xff, 0xd8, 0xff]);
        var uploaded = (await _service.UploadImageAsync("sunset", content, "image/jpeg", CancellationToken.None))!;
        Assert.StartsWith("/api/v1/tour-pages/sunset/images/", uploaded.Url, StringComparison.Ordinal);
        Assert.Equal("sunset", _images.LastPageKey);
        var imageId = Guid.Parse(uploaded.Url.Split('/')[^1]);
        Assert.NotNull(await _service.GetImageAsync("sunset", imageId, CancellationToken.None));
        Assert.Null(await _service.GetImageAsync("daytime", imageId, CancellationToken.None));
        var page = (await _service.GetAsync("sunset", false, CancellationToken.None))!;
        Assert.Null(page.Document.HeroImageUrl);
        await _service.UpdateAsync("sunset", page.Document with { HeroImageUrl = uploaded.Url }, CancellationToken.None);
        Assert.Equal(uploaded.Url, (await _service.GetAsync("sunset", false, CancellationToken.None))!.Document.HeroImageUrl);
    }

    [Fact]
    public async Task CategoryDefaultsRemainAvailableWhenProviderIsUnavailable()
    {
        _tours.Unavailable = true;
        var page = await _service.GetAsync("daytime", false, CancellationToken.None);
        Assert.NotNull(page);
        Assert.Equal("daytime", page.CategoryKey);
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FakeImages : ITourImageStorage, ITourPageImageStorage
    {
        public string? LastPageKey { get; private set; }
        public Task<string> SavePageImageAsync(string pageKey, Stream content, string contentType, CancellationToken cancellationToken)
        { LastPageKey = pageKey; return Task.FromResult($"tour-images/pages/{pageKey}/mock.jpg"); }
        public Task<TourImageFile?> GetAsync(string objectKey, string? contentType, CancellationToken cancellationToken)
            => Task.FromResult<TourImageFile?>(new([0xff, 0xd8, 0xff], "image/jpeg"));
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> SaveAsync(int externalTourId, Stream content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTours : ITourContentService
    {
        public bool Unavailable { get; set; }
        public Task<IReadOnlyList<AdminTourContentItem>> ListAdminAsync(CancellationToken cancellationToken)
            => Unavailable ? throw new TourCatalogUnavailableException("test") : Task.FromResult<IReadOnlyList<AdminTourContentItem>>([
                new(41, 1, "sunset", "Sunset", "Sunset source", "Sunset TR", "Sunset EN", null, null, null, null, null, false, 0, true, false, null),
                new(42, 2, "daytime", "Daytime", "Hidden tour", null, null, null, null, null, null, null, false, 1, false, false, null),
            ]);
        public Task<IReadOnlyList<PublicTourItem>> ListPublicAsync(CancellationToken cancellationToken)
            => Unavailable ? throw new TourCatalogUnavailableException("test") : Task.FromResult<IReadOnlyList<PublicTourItem>>([
                new(41, 1, "sunset", "Sunset", "Sunset source", "Sunset TR", "Sunset EN", null, null, null, null, null, 0),
            ]);
        public Task<AdminTourContentItem?> UpdateAsync(int externalTourId, UpdateTourContentCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdminTourContentItem?> UploadImageAsync(int externalTourId, Stream content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteImageAsync(int externalTourId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TourImageFile?> GetImageAsync(int externalTourId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
