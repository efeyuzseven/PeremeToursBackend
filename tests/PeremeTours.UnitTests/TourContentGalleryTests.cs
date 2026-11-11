using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PeremeTours.Application.Tours;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.UnitTests;

// Extra card images: saved in order, shown on the public tour list, and left alone by clients that do not send them.
public sealed class TourContentGalleryTests : IDisposable
{
    private static readonly string[] Images = ["/api/v1/tour-pages/tour-7/images/a", "/api/v1/tour-pages/tour-7/images/b"];
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PeremeToursDbContext _db;
    private readonly TourContentService _service;

    public TourContentGalleryTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<PeremeToursDbContext>().UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, TicketCancellationTests.SqliteTestModel>().Options);
        _db.Database.EnsureCreated();
        _service = new(_db, new Catalog(), new Storage());
    }

    [Fact]
    public async Task GalleryIsSavedInOrderAndShownPublicly()
    {
        Assert.Empty((await _service.ListPublicAsync(CancellationToken.None)).Single().GalleryImageUrls!);
        var saved = await _service.UpdateAsync(7, Command(Images), CancellationToken.None);
        Assert.Equal(Images, saved!.GalleryImageUrls);
        Assert.Equal(Images, (await _service.ListPublicAsync(CancellationToken.None)).Single().GalleryImageUrls);
        Assert.Equal(Images, (await _service.ListAdminAsync(CancellationToken.None)).Single().GalleryImageUrls);
    }

    [Fact]
    public async Task OmittedGalleryIsKeptAndEmptyGalleryClearsIt()
    {
        await _service.UpdateAsync(7, Command(Images), CancellationToken.None);
        var kept = await _service.UpdateAsync(7, Command(null), CancellationToken.None);
        Assert.Equal(Images, kept!.GalleryImageUrls);
        var cleared = await _service.UpdateAsync(7, Command([]), CancellationToken.None);
        Assert.Empty(cleared!.GalleryImageUrls!);
        Assert.Null((await _db.TourContents.SingleAsync()).GalleryImageUrlsJson);
    }

    private static UpdateTourContentCommand Command(IReadOnlyList<string>? gallery) =>
        new("Sunset", null, null, null, null, null, 0, true, gallery);

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class Catalog : ITourCatalogService
    {
        public Task<IReadOnlyList<TourCatalogItem>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TourCatalogItem>>([new(7, 1, TourCategoryKeys.Sunset, "Sunset", "Sunset source")]);
        public Task<IReadOnlyList<TourPort>> ListPortsAsync(int externalTourId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TourAvailability?> GetAvailabilityAsync(int externalTourId, int externalDeparturePortId, int saleType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Storage : ITourImageStorage
    {
        public Task<string> SaveAsync(int externalTourId, Stream content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TourImageFile?> GetAsync(string objectKey, string? contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
