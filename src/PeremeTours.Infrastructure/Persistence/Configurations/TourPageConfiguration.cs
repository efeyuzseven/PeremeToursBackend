using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Content;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class TourPageConfiguration : IEntityTypeConfiguration<TourPage>
{
    public void Configure(EntityTypeBuilder<TourPage> builder)
    {
        builder.ToTable("TourPages");
        builder.HasKey(page => page.Key);
        builder.Property(page => page.Key).HasMaxLength(80);
        builder.Property(page => page.ContentJson).HasColumnType("jsonb").IsRequired();
        builder.Property(page => page.UpdatedAtUtc).IsRequired();
    }
}

internal sealed class TourPageImageConfiguration : IEntityTypeConfiguration<TourPageImage>
{
    public void Configure(EntityTypeBuilder<TourPageImage> builder)
    {
        builder.ToTable("TourPageImages");
        builder.HasKey(image => image.Id);
        builder.Property(image => image.PageKey).HasMaxLength(80).IsRequired();
        builder.Property(image => image.ObjectKey).HasMaxLength(500).IsRequired();
        builder.Property(image => image.ContentType).HasMaxLength(80).IsRequired();
        builder.HasOne<TourPage>().WithMany().HasForeignKey(image => image.PageKey).OnDelete(DeleteBehavior.Cascade);
    }
}
