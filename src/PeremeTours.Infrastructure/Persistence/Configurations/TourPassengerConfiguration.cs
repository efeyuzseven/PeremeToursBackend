using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PeremeTours.Domain.Tickets;

namespace PeremeTours.Infrastructure.Persistence.Configurations;

internal sealed class TourPassengerConfiguration : IEntityTypeConfiguration<TourPassenger>
{
    public void Configure(EntityTypeBuilder<TourPassenger> builder)
    {
        builder.ToTable("TourPassengers");
        builder.HasKey(passenger => passenger.Id);
        builder.HasIndex(passenger => new { passenger.TourTicketId, passenger.Sequence }).IsUnique();
        builder.Property(passenger => passenger.UnitAmount).HasPrecision(12, 2);
        builder.Property(passenger => passenger.FirstName).HasMaxLength(80);
        builder.Property(passenger => passenger.LastName).HasMaxLength(80);
        builder.Property(passenger => passenger.Gender).HasMaxLength(6);
        builder.Property(passenger => passenger.Nationality).HasMaxLength(7);
        builder.Property(passenger => passenger.IdentityNumber).HasMaxLength(30);
        builder.Property(passenger => passenger.ExternalTicketGuid).HasMaxLength(128);
        builder.Property(passenger => passenger.Pnr).HasMaxLength(128);
    }
}
