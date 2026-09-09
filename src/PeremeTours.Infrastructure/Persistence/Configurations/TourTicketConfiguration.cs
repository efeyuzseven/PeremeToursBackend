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
        builder.Property(ticket => ticket.Amount).HasPrecision(12, 2);
        builder.Property(ticket => ticket.Currency).HasMaxLength(3).IsRequired();
        builder.Property(ticket => ticket.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(ticket => ticket.Channel).HasConversion<string>().HasMaxLength(32);
        builder.Property(ticket => ticket.CreatedAtUtc).IsRequired();
        builder.Property(ticket => ticket.UpdatedAtUtc).IsRequired();
        builder.HasIndex(ticket => ticket.CreatedAtUtc);
        builder.HasIndex(ticket => ticket.TourDate);
        builder.HasOne(ticket => ticket.User)
            .WithMany()
            .HasForeignKey(ticket => ticket.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
