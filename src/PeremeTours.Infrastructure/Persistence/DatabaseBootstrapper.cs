using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;

namespace PeremeTours.Infrastructure.Persistence;

public static class DatabaseBootstrapper
{
    public static async Task RunAsync(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        CancellationToken cancellationToken = default
    )
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var email = configuration["DeploymentBootstrap:AdminEmail"]
            ?? "admin@peremetours.com";
        var password = configuration["DeploymentBootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Deployment bootstrap admin password is missing."
            );
        }
        await SeedAdminAsync(
            dbContext,
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>(),
            email,
            password,
            cancellationToken
        );

        if (configuration.GetValue("DeploymentBootstrap:SeedDemoTickets", false))
        {
            await SeedTicketsAsync(dbContext, cancellationToken);
        }
    }

    private static async Task SeedAdminAsync(
        PeremeToursDbContext dbContext,
        IPasswordHasher<UserAccount> passwordHasher,
        string email,
        string password,
        CancellationToken cancellationToken
    )
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var account = await dbContext.Users.SingleOrDefaultAsync(
            user => user.NormalizedEmail == normalizedEmail,
            cancellationToken
        );
        if (account is not null)
        {
            account.Role = UserRoles.Admin;
            account.IsActive = true;
            account.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = email.Trim().ToLowerInvariant(),
            NormalizedEmail = normalizedEmail,
            FirstName = "PeremeTours",
            LastName = "Admin",
            PasswordHash = string.Empty,
            Role = UserRoles.Admin,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        account.PasswordHash = passwordHasher.HashPassword(account, password);
        dbContext.Users.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedTicketsAsync(
        PeremeToursDbContext dbContext,
        CancellationToken cancellationToken
    )
    {
        if (await dbContext.TourTickets.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        TourTicket[] tickets =
        [
            CreateDemoTicket("PRM-DEMO-001", "Boğaz & Saraylar Turu", "Selin Kaya", "selin@example.com", 2, 2900, TicketStatus.Confirmed, now),
            CreateDemoTicket("PRM-DEMO-002", "Gün Batımı Boğaz Turu", "Mert Yılmaz", "mert@example.com", 3, 4350, TicketStatus.Pending, now.AddHours(-1)),
            CreateDemoTicket("PRM-DEMO-003", "Adalar Kaçamağı", "Deniz Akın", "deniz@example.com", 2, 3600, TicketStatus.Confirmed, now.AddHours(-2)),
        ];
        dbContext.TourTickets.AddRange(tickets);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static TourTicket CreateDemoTicket(
        string code,
        string tourName,
        string customerName,
        string customerEmail,
        int guestCount,
        decimal amount,
        TicketStatus status,
        DateTimeOffset createdAt
    ) => new()
    {
        Id = Guid.NewGuid(),
        TicketCode = code,
        TourName = tourName,
        TourDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),
        DepartureTime = new TimeOnly(18, 30),
        CustomerName = customerName,
        CustomerEmail = customerEmail,
        GuestCount = guestCount,
        Amount = amount,
        Currency = "TRY",
        Status = status,
        Channel = TicketChannel.Web,
        CreatedAtUtc = createdAt,
        UpdatedAtUtc = createdAt,
    };
}
