using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class TicketErrorRecordConfiguration : IEntityTypeConfiguration<TicketErrorRecord>
{
    public void Configure(EntityTypeBuilder<TicketErrorRecord> builder)
    {
        builder.ToTable("TicketErrorRecords");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Stage).HasConversion<string>().HasMaxLength(32);
        builder.Property(item => item.Code).HasMaxLength(64).IsRequired();
        builder.Property(item => item.ProviderCode).HasMaxLength(64);
        builder.Property(item => item.Message).HasMaxLength(500).IsRequired();
        builder.HasIndex(item => item.CreatedAtUtc);
        builder.HasIndex(item => new { item.TicketId, item.CreatedAtUtc });
        builder.HasOne(item => item.Ticket).WithMany().HasForeignKey(item => item.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
