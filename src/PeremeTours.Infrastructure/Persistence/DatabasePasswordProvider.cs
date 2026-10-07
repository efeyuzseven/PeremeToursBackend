using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Npgsql;

namespace PeremeTours.Infrastructure.Persistence;

/// <summary>
/// Reads AWSCURRENT for each new physical connection, not each pooled checkout.
/// Never caches a rotated password or retries SQL/payment operations.
/// </summary>
internal sealed class DatabasePasswordProvider(IAmazonSecretsManager secrets, string secretArn)
{
    public async ValueTask<string> GetPasswordAsync(
        NpgsqlConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        var response = await secrets.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = secretArn,
            VersionStage = "AWSCURRENT",
        }, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response.SecretString))
            throw InvalidSecret();

        try
        {
            using var document = JsonDocument.Parse(response.SecretString);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("password", out var password)
                || password.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(password.GetString())
                || !root.TryGetProperty("username", out var username)
                || username.ValueKind != JsonValueKind.String
                || !string.Equals(username.GetString(), settings.Username, StringComparison.Ordinal))
                throw InvalidSecret();

            // RDS-managed secrets may omit endpoint metadata. If present, verify it.
            if (root.TryGetProperty("host", out var host)
                && (host.ValueKind != JsonValueKind.String
                    || !string.Equals(host.GetString(), settings.Host, StringComparison.OrdinalIgnoreCase)))
                throw InvalidSecret();

            return password.GetString()!;
        }
        catch (JsonException)
        {
            // Do not include secret JSON or a parser exception in application logs.
            throw InvalidSecret();
        }
    }

    private static InvalidOperationException InvalidSecret() =>
        new("The database credential secret is invalid or does not match the configured database user/host.");
}
