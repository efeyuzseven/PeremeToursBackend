using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tours;

namespace PeremeTours.Infrastructure.Content;

internal sealed class HomepageImageService(PeremeToursDbContext db, ITourImageStorage storage,
    ITourPageImageStorage uploads, TimeProvider clock) : IHomepageImageService
{
    public async Task<TourPageImageResult> UploadAsync(Stream content, string contentType, CancellationToken cancellationToken)
    {
        // Immutable, random keys: uploading a draft never replaces a published cover.
        var key = await uploads.SavePageImageAsync("homepage", content, contentType, cancellationToken);
        var image = new HomepageImage { Id = Guid.NewGuid(), ObjectKey = key, ContentType = contentType,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.HomepageImages.Add(image);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch { await storage.DeleteAsync(key, CancellationToken.None); throw; }
        return new TourPageImageResult($"/api/v1/site-content/homepage/images/{image.Id}");
    }

    public async Task<TourImageFile?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var image = await db.HomepageImages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return image is null ? null : await storage.GetAsync(image.ObjectKey, image.ContentType, cancellationToken);
    }
}
