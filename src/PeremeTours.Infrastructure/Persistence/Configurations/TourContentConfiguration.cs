using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tours;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class TourContentConfiguration
    : IEntityTypeConfiguration<TourContent>
{
    public void Configure(EntityTypeBuilder<TourContent> builder)
    {
        builder.ToTable("TourContents");
        builder.HasKey(content => content.ExternalTourId);
        builder.Property(content => content.ExternalTourId).ValueGeneratedNever();
        builder.Property(content => content.TitleTr).HasMaxLength(200);
        builder.Property(content => content.TitleEn).HasMaxLength(200);
        builder.Property(content => content.DescriptionTr).HasMaxLength(3000);
        builder.Property(content => content.DescriptionEn).HasMaxLength(3000);
        builder.Property(content => content.BadgeTr).HasMaxLength(80);
        builder.Property(content => content.BadgeEn).HasMaxLength(80);
        builder.Property(content => content.ImageObjectKey).HasMaxLength(500);
        builder.Property(content => content.ImageContentType).HasMaxLength(100);
        builder.Property(content => content.IsVisible).IsRequired();
        builder.Property(content => content.CreatedAtUtc).IsRequired();
        builder.Property(content => content.UpdatedAtUtc).IsRequired();
        builder.HasIndex(content => content.SortOrder);
    }
}
