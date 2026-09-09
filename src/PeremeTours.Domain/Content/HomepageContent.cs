namespace PeremeTours.Domain.Content;

public sealed class HomepageContent
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public required string ContentJson { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
