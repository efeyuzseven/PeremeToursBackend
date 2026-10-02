using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class PaymentEmailConfiguration : IEntityTypeConfiguration<PaymentEmail>
{
    public void Configure(EntityTypeBuilder<PaymentEmail> builder)
    {
        builder.ToTable("PaymentEmails");
        builder.HasKey(item => item.TicketId);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(item => item.LastFailureCode).HasMaxLength(64);
        builder.HasIndex(item => new { item.Status, item.NextAttemptAtUtc });
        builder.HasOne(item => item.Ticket).WithOne(ticket => ticket.PaymentEmail)
            .HasForeignKey<PaymentEmail>(item => item.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}
