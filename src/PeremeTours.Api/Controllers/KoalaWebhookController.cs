using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Content;

namespace PeremeTours.Api.Controllers;

// KoalaWriter's Custom Webhook integration posts finished articles here; each becomes a blog post.
[ApiController]
[AllowAnonymous]
[Route("api/v1/integrations/koala/webhook")]
public sealed class KoalaWebhookController(IKoalaWebhookReceiver receiver) : ControllerBase
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var result = await receiver.ReceiveAsync(buffer.ToArray(),
            Request.Headers["X-Koala-Signature"].FirstOrDefault(),
            Request.Headers["X-Koala-Event"].FirstOrDefault(),
            Request.Headers["X-Koala-Delivery-Id"].FirstOrDefault(),
            cancellationToken);
        // Koala retries only network errors and 5xx, so a refused delivery is a 4xx it records against the article.
        return result.Status switch
        {
            KoalaWebhookStatus.NotConfigured => NotFound(),
            KoalaWebhookStatus.BadSignature => Problem(statusCode: 401, title: "İmza doğrulanamadı."),
            KoalaWebhookStatus.InvalidPayload => Problem(statusCode: 400, title: "Makale verisi okunamadı.", detail: result.Detail),
            _ => Ok(new { status = result.Status.ToString().ToLowerInvariant(), id = result.PageId, slug = result.Slug, detail = result.Detail }),
        };
    }
}
