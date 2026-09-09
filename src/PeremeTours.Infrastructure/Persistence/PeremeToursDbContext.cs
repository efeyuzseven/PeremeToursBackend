using Microsoft.EntityFrameworkCore;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;

namespace PeremeTours.Infrastructure.Persistence;

public sealed class PeremeToursDbContext(DbContextOptions<PeremeToursDbContext> options)
    : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<TourTicket> TourTickets => Set<TourTicket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PeremeToursDbContext).Assembly);
    }
}
