namespace PeremeTours.Domain.Content;

public sealed class HomepageImage
{
    public Guid Id { get; set; }
    public required string ObjectKey { get; set; }
    public required string ContentType { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
