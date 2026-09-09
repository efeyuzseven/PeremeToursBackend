using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Content;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class HomepageContentConfiguration
    : IEntityTypeConfiguration<HomepageContent>
{
    public void Configure(EntityTypeBuilder<HomepageContent> builder)
    {
        builder.ToTable("HomepageContents");
        builder.HasKey(content => content.Id);
        builder.Property(content => content.Id).ValueGeneratedNever();
        builder.Property(content => content.ContentJson)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(content => content.UpdatedAtUtc).IsRequired();
    }
}
