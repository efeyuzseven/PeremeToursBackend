using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Content;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class FrequentlyAskedQuestionConfiguration
    : IEntityTypeConfiguration<FrequentlyAskedQuestion>
{
    public void Configure(EntityTypeBuilder<FrequentlyAskedQuestion> builder)
    {
        builder.ToTable("FrequentlyAskedQuestions");
        builder.HasKey(question => question.Id);
        builder.Property(question => question.QuestionTr)
            .HasMaxLength(300)
            .IsRequired();
        builder.Property(question => question.AnswerTr)
            .HasMaxLength(3000)
            .IsRequired();
        builder.Property(question => question.QuestionEn)
            .HasMaxLength(300)
            .IsRequired();
        builder.Property(question => question.AnswerEn)
            .HasMaxLength(3000)
            .IsRequired();
        builder.Property(question => question.SortOrder).IsRequired();
        builder.Property(question => question.IsPublished).IsRequired();
        builder.Property(question => question.CreatedAtUtc).IsRequired();
        builder.Property(question => question.UpdatedAtUtc).IsRequired();
        builder.HasIndex(question => new { question.IsPublished, question.SortOrder });
    }
}
