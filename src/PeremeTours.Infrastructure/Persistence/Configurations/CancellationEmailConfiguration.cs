using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class CancellationEmailConfiguration : IEntityTypeConfiguration<CancellationEmail>
{
    public void Configure(EntityTypeBuilder<CancellationEmail> builder)
    {
        builder.ToTable("CancellationEmails");
        builder.HasKey(item => item.TicketId);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(item => item.LastFailureCode).HasMaxLength(64);
        builder.HasIndex(item => new { item.Status, item.NextAttemptAtUtc });
        builder.HasOne(item => item.Ticket).WithOne(ticket => ticket.CancellationEmail)
            .HasForeignKey<CancellationEmail>(item => item.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}
