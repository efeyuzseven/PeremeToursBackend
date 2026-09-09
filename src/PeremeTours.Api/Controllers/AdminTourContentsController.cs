using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Tours;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/tour-contents")]
public sealed class AdminTourContentsController(
    ITourContentService tourContentService
) : ControllerBase
{
    private const long MaximumImageSize = 8 * 1024 * 1024;
    private static readonly HashSet<string> SupportedContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
        };

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AdminTourContentItem>>(
        StatusCodes.Status200OK
    )]
    public async Task<ActionResult<IReadOnlyList<AdminTourContentItem>>> List(
        CancellationToken cancellationToken
    ) => Ok(await tourContentService.ListAdminAsync(cancellationToken));

    [HttpPut("{externalTourId:int}")]
    [ProducesResponseType<AdminTourContentItem>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminTourContentItem>> Update(
        [Range(1, int.MaxValue)] int externalTourId,
        UpdateTourContentRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await tourContentService.UpdateAsync(
            externalTourId,
            new UpdateTourContentCommand(
                request.TitleTr,
                request.TitleEn,
                request.DescriptionTr,
                request.DescriptionEn,
                request.BadgeTr,
                request.BadgeEn,
                request.SortOrder,
                request.IsVisible
            ),
            cancellationToken
        );
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{externalTourId:int}/image")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumImageSize)]
    [ProducesResponseType<AdminTourContentItem>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(
        StatusCodes.Status400BadRequest
    )]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminTourContentItem>> UploadImage(
        [Range(1, int.MaxValue)] int externalTourId,
        [FromForm] TourImageUploadRequest request,
        CancellationToken cancellationToken
    )
    {
        if (
            request.File.Length is <= 0 or > MaximumImageSize
            || !SupportedContentTypes.Contains(request.File.ContentType)
        )
        {
            ModelState.AddModelError(
                nameof(request.File),
                "Görsel JPG, PNG veya WebP formatında ve en fazla 8 MB olmalıdır."
            );
            return ValidationProblem(ModelState);
        }

        await using var upload = new MemoryStream((int)request.File.Length);
        await request.File.CopyToAsync(upload, cancellationToken);
        var bytes = upload.ToArray();
        if (!HasValidSignature(bytes, request.File.ContentType))
        {
            ModelState.AddModelError(
                nameof(request.File),
                "Dosya içeriği belirtilen görsel formatıyla eşleşmiyor."
            );
            return ValidationProblem(ModelState);
        }

        await using var content = new MemoryStream(bytes, writable: false);
        var result = await tourContentService.UploadImageAsync(
            externalTourId,
            content,
            request.File.ContentType.ToLowerInvariant(),
            cancellationToken
        );
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("{externalTourId:int}/image")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteImage(
        [Range(1, int.MaxValue)] int externalTourId,
        CancellationToken cancellationToken
    ) => await tourContentService.DeleteImageAsync(
        externalTourId,
        cancellationToken
    ) ? NoContent() : NotFound();

    private static bool HasValidSignature(
        ReadOnlySpan<byte> content,
        string contentType
    ) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => content.Length >= 3
            && content[0] == 0xff
            && content[1] == 0xd8
            && content[2] == 0xff,
        "image/png" => content.Length >= 8
            && content[0] == 0x89
            && content[1] == 0x50
            && content[2] == 0x4e
            && content[3] == 0x47
            && content[4] == 0x0d
            && content[5] == 0x0a
            && content[6] == 0x1a
            && content[7] == 0x0a,
        "image/webp" => content.Length >= 12
            && content[..4].SequenceEqual("RIFF"u8)
            && content.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}

public sealed class UpdateTourContentRequest
{
    [StringLength(200)]
    public string? TitleTr { get; init; }

    [StringLength(200)]
    public string? TitleEn { get; init; }

    [StringLength(3000)]
    public string? DescriptionTr { get; init; }

    [StringLength(3000)]
    public string? DescriptionEn { get; init; }

    [StringLength(80)]
    public string? BadgeTr { get; init; }

    [StringLength(80)]
    public string? BadgeEn { get; init; }

    [Range(0, 9999)]
    public int SortOrder { get; init; }

    public bool IsVisible { get; init; } = true;
}

public sealed class TourImageUploadRequest
{
    [Required]
    public required IFormFile File { get; init; }
}
