using Microsoft.EntityFrameworkCore;
using PeremeTours.Application.Content;
using PeremeTours.Domain.Content;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.Infrastructure.Content;

internal sealed class FrequentlyAskedQuestionService(
    PeremeToursDbContext dbContext
) : IFrequentlyAskedQuestionService
{
    public async Task<IReadOnlyList<FrequentlyAskedQuestionSummary>> ListPublishedAsync(
        CancellationToken cancellationToken
    ) => await Query()
        .Where(question => question.IsPublished)
        .OrderBy(question => question.SortOrder)
        .ThenBy(question => question.Id)
        .Select(ToSummary())
        .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FrequentlyAskedQuestionSummary>> ListAdminAsync(
        CancellationToken cancellationToken
    ) => await Query()
        .OrderBy(question => question.SortOrder)
        .ThenBy(question => question.Id)
        .Select(ToSummary())
        .ToListAsync(cancellationToken);

    public async Task<FrequentlyAskedQuestionSummary> CreateAsync(
        SaveFrequentlyAskedQuestionCommand command,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var question = new FrequentlyAskedQuestion
        {
            QuestionTr = command.QuestionTr.Trim(),
            AnswerTr = command.AnswerTr.Trim(),
            QuestionEn = command.QuestionEn.Trim(),
            AnswerEn = command.AnswerEn.Trim(),
            SortOrder = command.SortOrder,
            IsPublished = command.IsPublished,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        dbContext.FrequentlyAskedQuestions.Add(question);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(question);
    }

    public async Task<FrequentlyAskedQuestionSummary?> UpdateAsync(
        int id,
        SaveFrequentlyAskedQuestionCommand command,
        CancellationToken cancellationToken
    )
    {
        var question = await dbContext.FrequentlyAskedQuestions.FindAsync(
            [id],
            cancellationToken
        );
        if (question is null)
        {
            return null;
        }

        question.QuestionTr = command.QuestionTr.Trim();
        question.AnswerTr = command.AnswerTr.Trim();
        question.QuestionEn = command.QuestionEn.Trim();
        question.AnswerEn = command.AnswerEn.Trim();
        question.SortOrder = command.SortOrder;
        question.IsPublished = command.IsPublished;
        question.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(question);
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken
    )
    {
        var deleted = await dbContext.FrequentlyAskedQuestions
            .Where(question => question.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    private IQueryable<FrequentlyAskedQuestion> Query() =>
        dbContext.FrequentlyAskedQuestions.AsNoTracking();

    private static System.Linq.Expressions.Expression<
        Func<FrequentlyAskedQuestion, FrequentlyAskedQuestionSummary>
    > ToSummary() => question => new FrequentlyAskedQuestionSummary(
        question.Id,
        question.QuestionTr,
        question.AnswerTr,
        question.QuestionEn,
        question.AnswerEn,
        question.SortOrder,
        question.IsPublished,
        question.CreatedAtUtc,
        question.UpdatedAtUtc
    );

    private static FrequentlyAskedQuestionSummary Map(
        FrequentlyAskedQuestion question
    ) => new(
        question.Id,
        question.QuestionTr,
        question.AnswerTr,
        question.QuestionEn,
        question.AnswerEn,
        question.SortOrder,
        question.IsPublished,
        question.CreatedAtUtc,
        question.UpdatedAtUtc
    );
}
