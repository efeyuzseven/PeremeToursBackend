using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Content;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class ContentPageConfiguration : IEntityTypeConfiguration<ContentPage>
{
    public void Configure(EntityTypeBuilder<ContentPage> builder)
    {
        builder.ToTable("ContentPages");
        builder.HasKey(page => page.Id);
        builder.Property(page => page.Kind).HasMaxLength(16).IsRequired();
        builder.Property(page => page.CoverImageUrl).HasMaxLength(2000);
        builder.Property(page => page.TitleTr).HasMaxLength(200).IsRequired();
        builder.Property(page => page.SlugTr).HasMaxLength(120).IsRequired();
        builder.Property(page => page.SummaryTr).HasMaxLength(500);
        builder.Property(page => page.BodyTr).IsRequired();
        builder.Property(page => page.MetaTitleTr).HasMaxLength(70);
        builder.Property(page => page.MetaDescriptionTr).HasMaxLength(200);
        builder.Property(page => page.TitleEn).HasMaxLength(200);
        builder.Property(page => page.SlugEn).HasMaxLength(120);
        builder.Property(page => page.SummaryEn).HasMaxLength(500);
        builder.Property(page => page.MetaTitleEn).HasMaxLength(70);
        builder.Property(page => page.MetaDescriptionEn).HasMaxLength(200);
        // Slugs are unique per kind and language; PostgreSQL treats NULL English slugs as distinct.
        builder.HasIndex(page => new { page.Kind, page.SlugTr }).IsUnique();
        builder.HasIndex(page => new { page.Kind, page.SlugEn }).IsUnique();
        builder.HasIndex(page => new { page.Kind, page.IsPublished, page.PublishedAtUtc });
    }
}
