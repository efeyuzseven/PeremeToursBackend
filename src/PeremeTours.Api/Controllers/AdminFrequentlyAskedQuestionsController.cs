using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/faqs")]
public sealed class AdminFrequentlyAskedQuestionsController(
    IFrequentlyAskedQuestionService questionService
) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FrequentlyAskedQuestionSummary>>(
        StatusCodes.Status200OK
    )]
    public async Task<ActionResult<IReadOnlyList<FrequentlyAskedQuestionSummary>>> List(
        CancellationToken cancellationToken
    ) => Ok(await questionService.ListAdminAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType<FrequentlyAskedQuestionSummary>(StatusCodes.Status201Created)]
    public async Task<ActionResult<FrequentlyAskedQuestionSummary>> Create(
        SaveFrequentlyAskedQuestionRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await questionService.CreateAsync(
            request.ToCommand(),
            cancellationToken
        );
        return Created($"/api/v1/admin/faqs/{result.Id}", result);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType<FrequentlyAskedQuestionSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FrequentlyAskedQuestionSummary>> Update(
        int id,
        SaveFrequentlyAskedQuestionRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await questionService.UpdateAsync(
            id,
            request.ToCommand(),
            cancellationToken
        );
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        CancellationToken cancellationToken
    ) => await questionService.DeleteAsync(id, cancellationToken)
        ? NoContent()
        : NotFound();
}

public sealed class SaveFrequentlyAskedQuestionRequest
{
    [Required, StringLength(300, MinimumLength = 2)]
    public required string QuestionTr { get; init; }

    [Required, StringLength(3000, MinimumLength = 2)]
    public required string AnswerTr { get; init; }

    [Required, StringLength(300, MinimumLength = 2)]
    public required string QuestionEn { get; init; }

    [Required, StringLength(3000, MinimumLength = 2)]
    public required string AnswerEn { get; init; }

    [Range(0, 9999)]
    public int SortOrder { get; init; }

    public bool IsPublished { get; init; } = true;

    public SaveFrequentlyAskedQuestionCommand ToCommand() => new(
        QuestionTr,
        AnswerTr,
        QuestionEn,
        AnswerEn,
        SortOrder,
        IsPublished
    );
}
