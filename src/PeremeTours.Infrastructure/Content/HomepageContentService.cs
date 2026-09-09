using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

internal sealed class HomepageContentService(
    PeremeToursDbContext dbContext
) : IHomepageContentService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<HomepageContentDocument> GetAsync(
        CancellationToken cancellationToken
    )
    {
        var json = await dbContext.HomepageContents
            .AsNoTracking()
            .Where(content => content.Id == HomepageContent.SingletonId)
            .Select(content => content.ContentJson)
            .SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(json)
            ? HomepageContentDefaults.Value
            : JsonSerializer.Deserialize<HomepageContentDocument>(
                json,
                JsonOptions
            ) ?? HomepageContentDefaults.Value;
    }

    public async Task<HomepageContentDocument> UpdateAsync(
        HomepageContentDocument document,
        CancellationToken cancellationToken
    )
    {
        var content = await dbContext.HomepageContents.FindAsync(
            [HomepageContent.SingletonId],
            cancellationToken
        );
        var now = DateTimeOffset.UtcNow;
        if (content is null)
        {
            content = new HomepageContent
            {
                ContentJson = string.Empty,
                UpdatedAtUtc = now,
            };
            dbContext.HomepageContents.Add(content);
        }

        content.ContentJson = JsonSerializer.Serialize(document, JsonOptions);
        content.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return document;
    }
}
