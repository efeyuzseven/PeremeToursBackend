using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/faqs")]
public sealed class FrequentlyAskedQuestionsController(
    IFrequentlyAskedQuestionService questionService
) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FrequentlyAskedQuestionSummary>>(
        StatusCodes.Status200OK
    )]
    public async Task<ActionResult<IReadOnlyList<FrequentlyAskedQuestionSummary>>> List(
        CancellationToken cancellationToken
    ) => Ok(await questionService.ListPublishedAsync(cancellationToken));
}
