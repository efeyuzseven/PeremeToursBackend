namespace PeremeTours.Domain.Content;

public sealed class TourPage
{
    public required string Key { get; set; }
    public required string ContentJson { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TourPageImage
{
    public Guid Id { get; set; }
    public required string PageKey { get; set; }
    public required string ObjectKey { get; set; }
    public required string ContentType { get; set; }
}
