using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PeremeTours.Application.Payments;
using PeremeTours.Infrastructure;
using PeremeTours.Infrastructure.Authentication;
using PeremeTours.Infrastructure.Email;
using PeremeTours.Infrastructure.Persistence;
using PeremeTours.Infrastructure.Payments;

var builder = WebApplication.CreateBuilder(args);
var isBootstrapCommand = args.Contains(
    "--bootstrap",
    StringComparer.OrdinalIgnoreCase
);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(allowIntegerValues: false)
    )
);
builder.Services.AddInfrastructure(builder.Configuration);

var jwtOptions = builder.Configuration
    .GetSection(JwtOptions.SectionName)
    .Get<JwtOptions>() ?? throw new InvalidOperationException(
        "JWT configuration is missing."
    );
if (Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
{
    throw new InvalidOperationException(
        "JWT signing key must contain at least 32 bytes."
    );
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtOptions.Key)
            ),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var identifier = context.Principal?.FindFirstValue(
                    ClaimTypes.NameIdentifier
                );
                var tokenRole = context.Principal?.FindFirstValue(
                    ClaimTypes.Role
                );
                if (!Guid.TryParse(identifier, out var userId))
                {
                    context.Fail("Token user identifier is invalid.");
                    return;
                }

                var dbContext = context.HttpContext.RequestServices
                    .GetRequiredService<PeremeToursDbContext>();
                var currentUser = await dbContext.Users
                    .AsNoTracking()
                    .Where(user => user.Id == userId)
                    .Select(user => new { user.IsActive, user.Role })
                    .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
                if (
                    currentUser is null
                    || !currentUser.IsActive
                    || currentUser.Role != tokenRole
                )
                {
                    context.Fail("User access has changed or is disabled.");
                }
            },
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("tour-quote", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }
        )
    );
    options.AddPolicy("payment-start", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }
        )
    );
});

const string frontendCorsPolicy = "Frontend";
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddPolicy(
        frontendCorsPolicy,
        policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
    )
);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

if (args.Contains("--reconcile-cancelled-payment", StringComparer.OrdinalIgnoreCase))
{
    string? CommandValue(string name)
    {
        var index = Array.FindIndex(args, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    var orderId = CommandValue("--reconcile-cancelled-payment");
    var bankTransaction = CommandValue("--expected-bank-transaction");
    if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(bankTransaction)
        || !decimal.TryParse(CommandValue("--expected-amount"), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var amount) || amount <= 0)
    {
        Console.WriteLine("CANCELLATION_RECONCILIATION_INVALID_ARGUMENTS");
        Environment.ExitCode = 1;
        return;
    }
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CancelledPaymentReconciler>()
            .ReconcileAsync(orderId, amount, bankTransaction, CancellationToken.None);
        Console.WriteLine("CANCELLATION_RECONCILIATION_COMPLETE");
    }
    catch (Exception exception) when (exception is PaymentValidationException or PaymentGatewayException
        or PaymentConfigurationException or HttpRequestException or OperationCanceledException)
    {
        // A failed command must never dump a raw bank response or secret into task logs.
        Console.WriteLine("CANCELLATION_RECONCILIATION_NOT_VERIFIED");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--check-mail", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--check-mail-auth", StringComparer.OrdinalIgnoreCase))
{
    var result = await MailConnectionDiagnostics.CheckAsync(
        app.Configuration.GetSection(MailOptions.SectionName).Get<MailOptions>() ?? new MailOptions(), CancellationToken.None,
        authenticate: args.Contains("--check-mail-auth", StringComparer.OrdinalIgnoreCase));
    Console.WriteLine(result);
    Environment.ExitCode = result is "SMTP_TLS_OK" or "SMTP_PLAINTEXT_OK" or "SMTP_TLS_AUTH_OK" or "SMTP_PLAINTEXT_AUTH_OK" ? 0 : 1;
    return;
}

if (isBootstrapCommand)
{
    await DatabaseBootstrapper.RunAsync(
        app.Services,
        app.Configuration
    );
    return;
}

if (
    app.Environment.IsDevelopment()
    && app.Configuration.GetValue("Database:AutoMigrate", false)
)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<PeremeToursDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseCors(frontendCorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.Run();

public partial class Program;
