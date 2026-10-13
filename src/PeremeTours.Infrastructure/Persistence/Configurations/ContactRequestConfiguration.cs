using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Content;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class ContactRequestConfiguration : IEntityTypeConfiguration<ContactRequest>
{
    public void Configure(EntityTypeBuilder<ContactRequest> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Email).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Topic).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Message).HasMaxLength(3000).IsRequired();
        builder.Property(item => item.Language).HasMaxLength(2).IsRequired();
        builder.Property(item => item.PayloadHash).HasMaxLength(64).IsRequired();
    }
}

internal sealed class ContactEmailConfiguration : IEntityTypeConfiguration<ContactEmail>
{
    public void Configure(EntityTypeBuilder<ContactEmail> builder)
    {
        builder.HasKey(item => item.Id);
        builder.Property(item => item.RecipientEmail).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(item => item.LastFailureCode).HasMaxLength(100);
        builder.HasIndex(item => new { item.RequestId, item.RecipientEmail }).IsUnique();
        builder.HasIndex(item => new { item.Status, item.NextAttemptAtUtc });
        builder.HasOne(item => item.Request).WithMany(item => item.Emails).HasForeignKey(item => item.RequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
