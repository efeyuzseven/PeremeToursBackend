using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PeremeTours.Infrastructure;
using PeremeTours.Infrastructure.Persistence;

namespace PeremeTours.UnitTests;

public sealed class DatabasePasswordProviderTests
{
    private const string TestSecretArn = "arn:aws:secretsmanager:eu-central-1:123456789012:secret:test-database";
    private static readonly string[] RotatedPasswords = ["before-rotation", "after-rotation"];

    [Fact]
    public async Task ReadsCurrentVersionAgainAfterRotationWithoutRestart()
    {
        using var secrets = new FakeSecretsClient();
        var provider = new DatabasePasswordProvider(secrets, TestSecretArn);
        var settings = new NpgsqlConnectionStringBuilder { Username = "test_user" };

        Assert.Equal("before-rotation", await provider.GetPasswordAsync(settings, CancellationToken.None));
        secrets.Password = "after-rotation";
        Assert.Equal("after-rotation", await provider.GetPasswordAsync(settings, CancellationToken.None));

        Assert.Equal(2, secrets.Requests.Count);
        Assert.All(secrets.Requests, request =>
        {
            Assert.Equal(TestSecretArn, request.SecretId);
            Assert.Equal("AWSCURRENT", request.VersionStage);
            Assert.Null(request.VersionId);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"username\":\"test_user\",\"password\":\"\"}")]
    [InlineData("{\"username\":\"wrong_user\",\"password\":\"sensitive-value\"}")]
    [InlineData("{\"username\":\"test_user\",\"password\":\"sensitive-value\",\"host\":\"wrong-host\"}")]
    public async Task RejectsInvalidSecretWithoutExposingValues(string? secretJson)
    {
        using var secrets = new FakeSecretsClient { UseRawJson = true, RawJson = secretJson };
        var provider = new DatabasePasswordProvider(secrets, TestSecretArn);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetPasswordAsync(new NpgsqlConnectionStringBuilder
            {
                Username = "test_user", Host = "expected-host",
            }, CancellationToken.None).AsTask());

        Assert.DoesNotContain("sensitive-value", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task HonorsCancellationAndDoesNotFallBackToAnOldPassword()
    {
        using var secrets = new FakeSecretsClient();
        var provider = new DatabasePasswordProvider(secrets, TestSecretArn);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetPasswordAsync(new NpgsqlConnectionStringBuilder
            {
                Username = "test_user",
            }, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task SecretServiceFailureDoesNotReusePreviouslyReadPassword()
    {
        using var secrets = new FakeSecretsClient();
        var provider = new DatabasePasswordProvider(secrets, TestSecretArn);
        var settings = new NpgsqlConnectionStringBuilder { Username = "test_user" };
        Assert.Equal("before-rotation", await provider.GetPasswordAsync(settings, CancellationToken.None));
        secrets.Fail = true;
        await Assert.ThrowsAsync<AmazonSecretsManagerException>(() =>
            provider.GetPasswordAsync(settings, CancellationToken.None).AsTask());
        Assert.Equal(2, secrets.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ignored-stale-password")]
    public void DynamicConfigurationDoesNotRequireOrEmbedStartupPassword(string? startupPassword)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Host"] = "database-host",
            ["Database:Username"] = "test_user",
            ["Database:Name"] = "test_database",
            ["Database:Password"] = startupPassword,
        }).Build();

        var settings = new NpgsqlConnectionStringBuilder(DependencyInjection.BuildConnectionString(configuration, useSecret: true));
        Assert.Null(settings.Password);
        Assert.Equal(SslMode.Require, settings.SslMode);
        if (startupPassword is null)
            Assert.Throws<InvalidOperationException>(() => DependencyInjection.BuildConnectionString(configuration, useSecret: false));
        else
            Assert.Equal(startupPassword, new NpgsqlConnectionStringBuilder(
                DependencyInjection.BuildConnectionString(configuration, useSecret: false)).Password);
    }

    [Fact]
    public void DynamicConfigurationRemovesLegacyPasswordAndPassfile()
    {
        const string connectionString = "Host=localhost;Database=test_database;Username=test_user;Password=stale-password;Passfile=old-password-file";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PeremeToursDatabase"] = connectionString,
        }).Build();

        var dynamicSettings = new NpgsqlConnectionStringBuilder(DependencyInjection.BuildConnectionString(configuration, useSecret: true));
        Assert.Null(dynamicSettings.Password);
        Assert.Null(dynamicSettings.Passfile);
        Assert.Equal(connectionString, DependencyInjection.BuildConnectionString(configuration, useSecret: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NpgsqlNewPhysicalConnectionsUseRotatedPassword(bool synchronous)
    {
        // A tiny PostgreSQL authentication fixture, not a live DB or AWS mutation.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receivedPasswords = new List<string>();
        var server = AcceptConnectionsAsync(listener, receivedPasswords, timeout.Token);
        using var secrets = new FakeSecretsClient();
        var passwords = new DatabasePasswordProvider(secrets, TestSecretArn);
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Username = "test_user", Database = "test_database", Pooling = false,
            SslMode = SslMode.Disable, GssEncryptionMode = GssEncryptionMode.Disable,
            Timeout = 5,
        }.ConnectionString);
        builder.ConfigureTypeLoading(options => options.EnableTypeLoading(false));
        builder.UsePasswordProvider(
            settings => passwords.GetPasswordAsync(settings, CancellationToken.None).AsTask().GetAwaiter().GetResult(),
            passwords.GetPasswordAsync);
        await using var dataSource = builder.Build();

        for (var index = 0; index < 2; index++)
        {
            if (index == 1) secrets.Password = "after-rotation";
            await using var connection = dataSource.CreateConnection();
            if (synchronous) connection.Open();
            else await connection.OpenAsync(timeout.Token);
        }
        await server;

        Assert.Equal(RotatedPasswords, receivedPasswords);
        Assert.Equal(2, secrets.Requests.Count);
        Assert.DoesNotContain("Password", dataSource.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AcceptConnectionsAsync(TcpListener listener, List<string> passwords, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 2; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            _ = await ReadPacketAsync(stream, cancellationToken); // startup packet
            await SendPacketAsync(stream, (byte)'R', Int32Bytes(3), cancellationToken); // cleartext password request
            var messageType = new byte[1];
            await stream.ReadExactlyAsync(messageType, cancellationToken);
            Assert.Equal((byte)'p', messageType[0]);
            var password = await ReadPacketAsync(stream, cancellationToken);
            passwords.Add(Encoding.UTF8.GetString(password.AsSpan(0, password.Length - 1)));
            await SendPacketAsync(stream, (byte)'R', Int32Bytes(0), cancellationToken);
            await SendPacketAsync(stream, (byte)'S', Encoding.UTF8.GetBytes("server_version\0" + "16.0\0"), cancellationToken);
            await SendPacketAsync(stream, (byte)'S', Encoding.UTF8.GetBytes("client_encoding\0UTF8\0"), cancellationToken);
            await SendPacketAsync(stream, (byte)'K', [.. Int32Bytes(123), .. Int32Bytes(456)], cancellationToken);
            await SendPacketAsync(stream, (byte)'Z', [(byte)'I'], cancellationToken);
            await stream.ReadExactlyAsync(messageType, cancellationToken);
            Assert.Equal((byte)'X', messageType[0]); // close, without any SQL being executed
            _ = await ReadPacketAsync(stream, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, cancellationToken);
        var body = new byte[IPAddress.NetworkToHostOrder(BitConverter.ToInt32(length)) - 4];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return body;
    }

    private static async Task SendPacketAsync(Stream stream, byte type, byte[] body, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { type }, cancellationToken);
        await stream.WriteAsync(Int32Bytes(body.Length + 4), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static byte[] Int32Bytes(int value) => BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value));

    private sealed class FakeSecretsClient() : AmazonSecretsManagerClient(new AnonymousAWSCredentials(), new AmazonSecretsManagerConfig
    {
        RegionEndpoint = Amazon.RegionEndpoint.EUCentral1,
    })
    {
        public string Password { get; set; } = "before-rotation";
        public bool UseRawJson { get; set; }
        public string? RawJson { get; set; }
        public bool Fail { get; set; }
        public List<GetSecretValueRequest> Requests { get; } = [];

        public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Fail) throw new AmazonSecretsManagerException("Simulated secret service failure.");
            return Task.FromResult(new GetSecretValueResponse
            {
                SecretString = UseRawJson ? RawJson : JsonSerializer.Serialize(new { username = "test_user", password = Password }),
            });
        }
    }
}
