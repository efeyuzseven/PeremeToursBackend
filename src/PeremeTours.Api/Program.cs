using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PeremeTours.Infrastructure;
using PeremeTours.Infrastructure.Authentication;
using PeremeTours.Infrastructure.Persistence;

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
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.Run();

public partial class Program;
