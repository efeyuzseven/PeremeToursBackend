using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class TourTicketConfiguration : IEntityTypeConfiguration<TourTicket>
{
    public void Configure(EntityTypeBuilder<TourTicket> builder)
    {
        builder.ToTable("TourTickets");
        builder.HasKey(ticket => ticket.Id);
        builder.Property(ticket => ticket.TicketCode).HasMaxLength(32).IsRequired();
        builder.HasIndex(ticket => ticket.TicketCode).IsUnique();
        builder.Property(ticket => ticket.TourName).HasMaxLength(160).IsRequired();
        builder.Property(ticket => ticket.CustomerName).HasMaxLength(160).IsRequired();
        builder.Property(ticket => ticket.CustomerEmail).HasMaxLength(320).IsRequired();
        builder.Property(ticket => ticket.CustomerPhone).HasMaxLength(32);
        builder.Property(ticket => ticket.Amount).HasPrecision(12, 2);
        builder.Property(ticket => ticket.Currency).HasMaxLength(3).IsRequired();
        builder.Property(ticket => ticket.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(ticket => ticket.Channel).HasConversion<string>().HasMaxLength(32);
        builder.Property(ticket => ticket.PaymentStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(TicketPaymentStatus.NotRequired);
        builder.Property(ticket => ticket.PaymentProvider).HasMaxLength(32);
        builder.Property(ticket => ticket.BankAuthCode).HasMaxLength(64);
        builder.Property(ticket => ticket.BankHostReference).HasMaxLength(128);
        builder.Property(ticket => ticket.PaymentFailureCode).HasMaxLength(64);
        builder.Property(ticket => ticket.PaymentFailureMessage).HasMaxLength(500);
        builder.Property(ticket => ticket.CreatedAtUtc).IsRequired();
        builder.Property(ticket => ticket.UpdatedAtUtc).IsRequired();
        builder.HasIndex(ticket => ticket.CreatedAtUtc);
        builder.HasIndex(ticket => ticket.TourDate);
        builder.HasIndex(ticket => ticket.PaymentStatus);
        builder.HasIndex(ticket => ticket.BankHostReference);
        builder.HasOne(ticket => ticket.User)
            .WithMany()
            .HasForeignKey(ticket => ticket.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
