using Microsoft.EntityFrameworkCore;
using PeremeTours.Domain.Content;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Tours;
using PeremeTours.Domain.Users;

namespace PeremeTours.Infrastructure.Persistence;

public sealed class PeremeToursDbContext(DbContextOptions<PeremeToursDbContext> options)
    : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<TourTicket> TourTickets => Set<TourTicket>();

    public DbSet<TourContent> TourContents => Set<TourContent>();

    public DbSet<HomepageContent> HomepageContents => Set<HomepageContent>();

    public DbSet<FrequentlyAskedQuestion> FrequentlyAskedQuestions =>
        Set<FrequentlyAskedQuestion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PeremeToursDbContext).Assembly);
    }
}
