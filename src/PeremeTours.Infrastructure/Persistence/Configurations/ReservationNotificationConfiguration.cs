using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class ReservationNotificationConfiguration : IEntityTypeConfiguration<ReservationNotification>
{
    public void Configure(EntityTypeBuilder<ReservationNotification> builder)
    {
        builder.ToTable("ReservationNotifications");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.RecipientEmail).HasMaxLength(320).IsRequired();
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(item => item.LastFailureCode).HasMaxLength(64);
        builder.HasIndex(item => new { item.TicketId, item.RecipientEmail }).IsUnique();
        builder.HasIndex(item => new { item.Status, item.NextAttemptAtUtc });
        builder.HasOne(item => item.Ticket).WithMany().HasForeignKey(item => item.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
