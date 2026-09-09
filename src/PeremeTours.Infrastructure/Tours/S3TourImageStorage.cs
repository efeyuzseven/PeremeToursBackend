using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using PeremeTours.Application.Tours;

namespace PeremeTours.Infrastructure.Tours;

internal interface ITourImageStorage
{
    Task<string> SaveAsync(
        int externalTourId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken
    );

    Task<TourImageFile?> GetAsync(
        string objectKey,
        string? contentType,
        CancellationToken cancellationToken
    );

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

internal sealed class S3TourImageStorage(
    IAmazonS3 amazonS3,
    IOptions<TourImageStorageOptions> options
) : ITourImageStorage
{
    private readonly TourImageStorageOptions _options = options.Value;

    public async Task<string> SaveAsync(
        int externalTourId,
        Stream content,
        string contentType,
        CancellationToken cancellationToken
    )
    {
        EnsureConfigured();
        var extension = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new ArgumentException(
                "Unsupported tour image content type.",
                nameof(contentType)
            ),
        };
        var prefix = _options.Prefix.Trim('/');
        var objectKey = $"{prefix}/{externalTourId}/{Guid.NewGuid():N}{extension}";
        await amazonS3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            },
            cancellationToken
        );
        return objectKey;
    }

    public async Task<TourImageFile?> GetAsync(
        string objectKey,
        string? contentType,
        CancellationToken cancellationToken
    )
    {
        EnsureConfigured();
        try
        {
            using var response = await amazonS3.GetObjectAsync(
                _options.BucketName,
                objectKey,
                cancellationToken
            );
            await using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return new TourImageFile(
                buffer.ToArray(),
                contentType ?? response.Headers.ContentType ?? "application/octet-stream"
            );
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.NotFound
        )
        {
            return null;
        }
    }

    public async Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken
    )
    {
        EnsureConfigured();
        await amazonS3.DeleteObjectAsync(
            _options.BucketName,
            objectKey,
            cancellationToken
        );
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.BucketName))
        {
            throw new InvalidOperationException(
                "Tour image storage bucket is not configured."
            );
        }
    }
}
