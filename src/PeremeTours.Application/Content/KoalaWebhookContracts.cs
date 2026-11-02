namespace PeremeTours.Application.Content;

public enum KoalaWebhookStatus
{
    // No signing secret is configured, so no delivery is accepted.
    NotConfigured,
    BadSignature,
    InvalidPayload,
    Test,
    Created,
    Updated,
    // A non-Turkish article; Turkish is the site's required language.
    Skipped,
}

public sealed record KoalaWebhookResult(KoalaWebhookStatus Status, Guid? PageId = null, string? Slug = null, string? Detail = null);

// Receives KoalaWriter's "Custom Webhook" deliveries and stores each finished article as a blog post.
public interface IKoalaWebhookReceiver
{
    Task<KoalaWebhookResult> ReceiveAsync(byte[] body, string? signature, string? eventName, string? deliveryId,
        CancellationToken cancellationToken);
}
