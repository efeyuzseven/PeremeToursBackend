using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class TicketCancellationConfiguration : IEntityTypeConfiguration<TicketCancellation>
{
    public void Configure(EntityTypeBuilder<TicketCancellation> builder)
    {
        builder.ToTable("TicketCancellations");
        builder.HasKey(item => item.TicketId); // One durable cancellation claim per booking, including ambiguous attempts.
        builder.HasOne(item => item.Ticket).WithOne(item => item.Cancellation)
            .HasForeignKey<TicketCancellation>(item => item.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(item => item.Reason).HasMaxLength(300).IsRequired();
        builder.Property(item => item.Amount).HasPrecision(12, 2);
        builder.Property(item => item.BankOperation).HasMaxLength(10);
        builder.Property(item => item.BankTransactionId).HasMaxLength(128);
        builder.Property(item => item.BankReversalTransactionId).HasMaxLength(128);
        builder.Property(item => item.FailureCode).HasMaxLength(64);
        builder.Property(item => item.ProviderFailureCode).HasMaxLength(64);
        builder.HasIndex(item => item.Status);
        builder.HasIndex(item => new { item.ProviderStatus, item.ProviderNextAttemptAtUtc });
    }
}
