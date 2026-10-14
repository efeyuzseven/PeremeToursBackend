using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/feedback")]
public sealed class AdminContactFeedbackController(IContactFeedbackService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ContactFeedbackPage>> List([FromQuery, StringLength(160)] string? search,
        [FromQuery, EnumDataType(typeof(ContactFeedbackFilter))] ContactFeedbackFilter? filter,
        [FromQuery, Range(1, 100000)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await service.ListAsync(search, filter, page, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContactFeedbackDetail>> Get(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await service.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{id:guid}/replies")]
    [EnableRateLimiting("feedback-reply")]
    [RequestSizeLimit(32 * 1024)]
    public async Task<ActionResult<ContactReplyReceipt>> Reply(Guid id, ContactReplySubmission request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)) return Forbid();
        if (request.ReplyId == Guid.Empty || string.IsNullOrWhiteSpace(request.Body) || request.Body.Contains('\0'))
            return Problem(statusCode: 400, title: "FEEDBACK_REPLY_INVALID");
        try
        {
            var result = await service.ReplyAsync(id, adminId, request, cancellationToken);
            return result is null ? NotFound() : Accepted(result);
        }
        catch (ContactFeedbackForbiddenException) { return Forbid(); }
        catch (ContactFeedbackConflictException) { return Problem(statusCode: 409, title: "FEEDBACK_REPLY_CONFLICT"); }
        catch (ContactReplyBlockedException) { return Problem(statusCode: 409, title: "FEEDBACK_REPLY_PENDING"); }
        catch (ContactMailUnavailableException) { return Problem(statusCode: 503, title: "CONTACT_MAIL_UNAVAILABLE"); }
    }
}
