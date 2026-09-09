using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PeremeTours.Application.Authentication;
using PeremeTours.Application.Tickets;
using PeremeTours.Application.Users;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Authentication;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Tickets;
using PeremeTours.Infrastructure.Users;

namespace PeremeTours.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var connectionString = BuildConnectionString(configuration);

        services.AddDbContext<PeremeToursDbContext>(options =>
            options.UseNpgsql(connectionString)
        );
        services.Configure<JwtOptions>(
            configuration.GetSection(JwtOptions.SectionName)
        );
        services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddScoped<IAccessTokenService, JwtAccessTokenService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<ITicketService, TicketService>();
        return services;
    }

    private static string BuildConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString(
            "PeremeToursDatabase"
        );
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var host = configuration["Database:Host"];
        var username = configuration["Database:Username"];
        var password = configuration["Database:Password"];
        var database = configuration["Database:Name"];
        if (
            string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(database)
        )
        {
            throw new InvalidOperationException(
                "PostgreSQL connection settings are missing."
            );
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = configuration.GetValue("Database:Port", 5432),
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Require,
            Timeout = 15,
            CommandTimeout = 30,
        }.ConnectionString;
    }
}
