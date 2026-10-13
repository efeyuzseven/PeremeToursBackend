using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PeremeTours.Application.Content;

namespace PeremeTours.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/contact-requests")]
public sealed class ContactRequestsController(IContactRequestService service) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting("contact-request")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<ContactRequestReceipt>> Submit(ContactRequestSubmission request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!string.IsNullOrEmpty(request.Website)) return Accepted(new ContactRequestReceipt(request.RequestId));
        if (request.RequestId == Guid.Empty || new[] { request.Name, request.Email, request.Topic }.Any(item => string.IsNullOrWhiteSpace(item)
                || item.Any(char.IsControl)) || string.IsNullOrWhiteSpace(request.Message)
                || request.Message.Contains('\0') || !MimeKit.MailboxAddress.TryParse(request.Email.Trim(), out var mailbox)
                || !mailbox.Address.Equals(request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            return Problem(statusCode: 400, title: "CONTACT_REQUEST_INVALID", detail: "İletişim bilgilerini kontrol edin.");
        try { return Accepted(await service.SubmitAsync(request, cancellationToken)); }
        catch (ContactRequestConflictException) { return Problem(statusCode: 409, title: "CONTACT_REQUEST_CONFLICT"); }
        catch (ContactMailUnavailableException) { return Problem(statusCode: 503, title: "CONTACT_MAIL_UNAVAILABLE"); }
    }
}
