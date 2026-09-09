using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PeremeTours.Infrastructure.Persistence;

public sealed class PeremeToursDbContextFactory
    : IDesignTimeDbContextFactory<PeremeToursDbContext>
{
    public PeremeToursDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "PEREMETOURS_DESIGN_CONNECTION"
        ) ?? "Host=localhost;Port=5432;Database=peremetours;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<PeremeToursDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PeremeToursDbContext(options);
    }
}
