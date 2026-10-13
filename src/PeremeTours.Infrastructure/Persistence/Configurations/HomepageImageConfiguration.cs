using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Content;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class HomepageImageConfiguration : IEntityTypeConfiguration<HomepageImage>
{
    public void Configure(EntityTypeBuilder<HomepageImage> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ObjectKey).HasMaxLength(500).IsRequired();
        builder.Property(item => item.ContentType).HasMaxLength(80).IsRequired();
    }
}
