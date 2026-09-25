using Amazon;
using Amazon.S3;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PeremeTours.Application.Authentication;
using PeremeTours.Application.Content;
using PeremeTours.Application.Payments;
using PeremeTours.Application.Tickets;
using PeremeTours.Application.Tours;
using PeremeTours.Application.Users;
using PeremeTours.Domain.Users;
using PeremeTours.Infrastructure.Authentication;
using PeremeTours.Infrastructure.Content;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Payments;
using PeremeTours.Infrastructure.Tickets;
using PeremeTours.Infrastructure.Tours;
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
        services.AddScoped<ITourPaymentService, TourPaymentService>();
        services.Configure<ZiraatPosOptions>(
            configuration.GetSection(ZiraatPosOptions.SectionName)
        );
        services.AddHttpClient<IZiraatPosGateway, ZiraatPosGateway>(client =>
            client.Timeout = TimeSpan.FromSeconds(30)
        );
        services.AddScoped<ITourContentService, TourContentService>();
        services.AddScoped<IHomepageContentService, HomepageContentService>();
        services.AddScoped<ITourImageStorage, S3TourImageStorage>();
        services.AddMemoryCache();
        services.Configure<EasyTicketOptions>(
            configuration.GetSection(EasyTicketOptions.SectionName)
        );
        services.Configure<TourImageStorageOptions>(
            configuration.GetSection(TourImageStorageOptions.SectionName)
        );
        var imageStorageOptions = configuration
            .GetSection(TourImageStorageOptions.SectionName)
            .Get<TourImageStorageOptions>() ?? new TourImageStorageOptions();
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            RegionEndpoint.GetBySystemName(imageStorageOptions.Region)
        ));
        services.AddHttpClient<ITourCatalogService, EasyTicketTourCatalogService>(
            (serviceProvider, client) =>
            {
                var easyTicket = serviceProvider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<EasyTicketOptions>>()
                    .Value;
                if (Uri.TryCreate(easyTicket.BaseUrl, UriKind.Absolute, out var baseUri))
                {
                    client.BaseAddress = baseUri;
                }
                client.Timeout = TimeSpan.FromSeconds(15);
            }
        );
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
