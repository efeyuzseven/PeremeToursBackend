using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.ContentManagers)]
[Route("api/v1/admin/content-pages")]
public sealed class AdminContentPagesController(IContentPageService pages) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ContentPageSummary>>> List([FromQuery] string kind, CancellationToken cancellationToken)
    {
        if (!ContentPageKinds.IsValid(kind)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await pages.ListAdminAsync(kind, cancellationToken));
    }

    [HttpPost]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<ActionResult<ContentPageSummary>> Create(SaveContentPageRequest request, CancellationToken cancellationToken)
    {
        if (!Validate(request)) return ValidationProblem(ModelState);
        return await Save(async () =>
        {
            var result = await pages.CreateAsync(request.ToCommand(), cancellationToken);
            return Created($"/api/v1/admin/content-pages/{result.Id}", result);
        });
    }

    [HttpPut("{id:guid}")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<ActionResult<ContentPageSummary>> Update(Guid id, SaveContentPageRequest request, CancellationToken cancellationToken)
    {
        if (!Validate(request)) return ValidationProblem(ModelState);
        return await Save(async () =>
        {
            var result = await pages.UpdateAsync(id, request.ToCommand(), cancellationToken);
            return result is null ? NotFound() : Ok(result);
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => await pages.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    private async Task<ActionResult<ContentPageSummary>> Save(Func<Task<ActionResult<ContentPageSummary>>> action)
    {
        try { return await action(); }
        catch (ContentPageSlugConflictException exception)
        {
            return Problem(statusCode: 409, title: exception.Language == "en"
                ? "Bu İngilizce adres (slug) başka bir içerikte kullanılıyor."
                : "Bu adres (slug) başka bir içerikte kullanılıyor.");
        }
        catch (ContentPageReservedSlugException exception)
        {
            return Problem(statusCode: 400, title: $"“{exception.Slug}” adresi sitede zaten kullanılıyor; başka bir adres seçin.");
        }
    }

    private bool Validate(SaveContentPageRequest request)
    {
        if (!ContentPageKinds.IsValid(request.Kind)) ModelState.AddModelError(nameof(request.Kind), "İçerik türü geçersiz.");
        if (string.IsNullOrWhiteSpace(request.Tr.Title) || request.Tr.Title.Trim().Length < 2)
            ModelState.AddModelError(nameof(request.Tr), "Türkçe başlık en az 2 karakter olmalıdır.");
        if (string.IsNullOrWhiteSpace(request.Tr.Body))
            ModelState.AddModelError(nameof(request.Tr), "Türkçe içerik boş olamaz.");
        if (!string.IsNullOrEmpty(request.CoverImageUrl) && !AdminSiteContentController.IsImageUrl(request.CoverImageUrl))
            ModelState.AddModelError(nameof(request.CoverImageUrl), "Kapak görseli geçersiz.");
        if (request.En is { } en && !string.IsNullOrWhiteSpace(en.Title) && string.IsNullOrWhiteSpace(en.Body))
            ModelState.AddModelError(nameof(request.En), "İngilizce başlık girildiyse İngilizce içerik de girilmelidir.");
        return ModelState.IsValid;
    }
}

public sealed class SaveContentPageRequest
{
    [Required] public required string Kind { get; init; }
    public bool IsPublished { get; init; }
    public DateTimeOffset? PublishedAtUtc { get; init; }
    [StringLength(2000)] public string? CoverImageUrl { get; init; }
    public bool ShowInFooter { get; init; }
    [Range(0, 9999)] public int SortOrder { get; init; }
    [Required] public required SaveContentPageTextRequest Tr { get; init; }
    public SaveContentPageTextRequest? En { get; init; }

    public SaveContentPageCommand ToCommand() => new(Kind, IsPublished, PublishedAtUtc, CoverImageUrl, ShowInFooter, SortOrder, Tr.ToText(),
        En is null || string.IsNullOrWhiteSpace(En.Title) ? null : En.ToText());
}

public sealed class SaveContentPageTextRequest
{
    [StringLength(200)] public string? Title { get; init; }
    [StringLength(200)] public string? Slug { get; init; }
    [StringLength(500)] public string? Summary { get; init; }
    [StringLength(100_000)] public string? Body { get; init; }
    [StringLength(70)] public string? MetaTitle { get; init; }
    [StringLength(200)] public string? MetaDescription { get; init; }

    public SaveContentPageText ToText() => new(Title ?? "", Slug, Summary, Body ?? "", MetaTitle, MetaDescription);
}
