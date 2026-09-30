namespace PeremeTours.Application.Content;

public sealed record FrequentlyAskedQuestionSummary(
    int Id,
    string QuestionTr,
    string AnswerTr,
    string QuestionEn,
    string AnswerEn,
    int SortOrder,
    bool IsPublished,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

public sealed record SaveFrequentlyAskedQuestionCommand(
    string QuestionTr,
    string AnswerTr,
    string QuestionEn,
    string AnswerEn,
    int SortOrder,
    bool IsPublished
);

public interface IFrequentlyAskedQuestionService
{
    Task<IReadOnlyList<FrequentlyAskedQuestionSummary>> ListPublishedAsync(
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<FrequentlyAskedQuestionSummary>> ListAdminAsync(
        CancellationToken cancellationToken
    );

    Task<FrequentlyAskedQuestionSummary> CreateAsync(
        SaveFrequentlyAskedQuestionCommand command,
        CancellationToken cancellationToken
    );

    Task<FrequentlyAskedQuestionSummary?> UpdateAsync(
        int id,
        SaveFrequentlyAskedQuestionCommand command,
        CancellationToken cancellationToken
    );

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}
