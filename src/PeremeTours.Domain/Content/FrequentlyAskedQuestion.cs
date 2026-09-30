namespace PeremeTours.Domain.Content;

public sealed class FrequentlyAskedQuestion
{
    public int Id { get; set; }

    public required string QuestionTr { get; set; }

    public required string AnswerTr { get; set; }

    public required string QuestionEn { get; set; }

    public required string AnswerEn { get; set; }

    public int SortOrder { get; set; }

    public bool IsPublished { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
