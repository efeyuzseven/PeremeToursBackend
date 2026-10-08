using Amazon;
using Amazon.S3;
using Amazon.SecretsManager;
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
using PeremeTours.Infrastructure.Email;
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
        var secretArn = configuration["Database:SecretArn"];
        var connectionString = BuildConnectionString(configuration, !string.IsNullOrWhiteSpace(secretArn));
        if (!string.IsNullOrWhiteSpace(secretArn))
        {
            services.AddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient(
                new AmazonSecretsManagerConfig
                {
                    RegionEndpoint = RegionEndpoint.GetBySystemName(
                        configuration["Database:SecretRegion"] ?? "eu-central-1"),
                    Timeout = TimeSpan.FromSeconds(5),
                    MaxErrorRetry = 1,
                }));
            services.AddSingleton(provider => new DatabasePasswordProvider(
                provider.GetRequiredService<IAmazonSecretsManager>(), secretArn));
        }
        services.AddSingleton(provider =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            if (!string.IsNullOrWhiteSpace(secretArn))
            {
                var passwords = provider.GetRequiredService<DatabasePasswordProvider>();
                dataSourceBuilder.UsePasswordProvider(
                    settings => passwords.GetPasswordAsync(settings, CancellationToken.None)
                        .AsTask().GetAwaiter().GetResult(),
                    passwords.GetPasswordAsync);
            }
            return dataSourceBuilder.Build();
        });
        services.AddDbContext<PeremeToursDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>())
        );
        services.Configure<JwtOptions>(
            configuration.GetSection(JwtOptions.SectionName)
        );
        services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddScoped<IAccessTokenService, JwtAccessTokenService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<ITicketService, TicketService>();
        services.AddScoped<ITicketErrorService, TicketErrorService>();
        services.AddScoped<ITicketCancellationService, TicketCancellationService>();
        services.AddScoped<ProviderCancellationProcessor>();
        services.AddHostedService<ProviderCancellationWorker>();
        services.AddScoped<ITourPaymentService, TourPaymentService>();
        services.Configure<MailOptions>(configuration.GetSection(MailOptions.SectionName));
        services.AddScoped<IPaymentEmailSender, SmtpPaymentEmailSender>();
        services.AddScoped<PaymentEmailProcessor>();
        services.AddHostedService<PaymentEmailWorker>();
        services.AddScoped<IReservationNotificationSender, SmtpPaymentEmailSender>();
        services.AddScoped<ReservationNotificationProcessor>();
        services.AddHostedService<ReservationNotificationWorker>();
        services.AddSingleton<ThreeDSecureFrameStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ITourBookingService, TourBookingService>();
        services.Configure<ZiraatPosOptions>(
            configuration.GetSection(ZiraatPosOptions.SectionName)
        );
        services.AddHttpClient<IZiraatPosGateway, ZiraatPosGateway>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 1_000_000;
        });
        services.AddHttpClient<CancelledPaymentReconciler>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 256_000;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IBankCancellationGateway, ZiraatCancellationGateway>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(25);
            client.MaxResponseContentBufferSize = 256_000;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IEasyTicketCancellationGateway, EasyTicketCancellationGateway>((provider, client) =>
        {
            var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<EasyTicketOptions>>().Value;
            if (Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri)) client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(20);
            client.MaxResponseContentBufferSize = 131_072;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IEasyTicketSalesGateway, EasyTicketSalesGateway>((provider, client) =>
        {
            var easyTicket = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<EasyTicketOptions>>().Value;
            if (Uri.TryCreate(easyTicket.BaseUrl, UriKind.Absolute, out var baseUri)) client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(20);
            client.MaxResponseContentBufferSize = 131_072;
        });
        services.AddScoped<ITourContentService, TourContentService>();
        services.AddScoped<IHomepageContentService, HomepageContentService>();
        services.AddScoped<ITourPageService, TourPageService>();
        services.AddScoped<ITourPageImageStorage, S3TourImageStorage>();
        services.AddScoped<
            IFrequentlyAskedQuestionService,
            FrequentlyAskedQuestionService
        >();
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

    internal static string BuildConnectionString(IConfiguration configuration, bool useSecret)
    {
        var configured = configuration.GetConnectionString(
            "PeremeToursDatabase"
        );
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!useSecret) return configured;
            var settings = new NpgsqlConnectionStringBuilder(configured);
            settings.Remove("Password");
            settings.Remove("Passfile");
            return settings.ConnectionString;
        }

        var host = configuration["Database:Host"];
        var username = configuration["Database:Username"];
        var password = configuration["Database:Password"];
        var database = configuration["Database:Name"];
        if (
            string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(username)
            || (!useSecret && string.IsNullOrWhiteSpace(password))
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
            Password = useSecret ? null : password,
            SslMode = SslMode.Require,
            Timeout = 15,
            CommandTimeout = 30,
        }.ConnectionString;
    }
}
