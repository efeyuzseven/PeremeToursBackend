using System.Net;
using System.Net.Sockets;
using MailKit.Security;
using PeremeTours.Infrastructure.Email;

namespace PeremeTours.UnitTests;

public sealed class MailConnectionTests
{
    [Fact]
    public void DefaultConnectionRequiresStartTls()
    {
        Assert.Equal(SecureSocketOptions.StartTls,
            SmtpPaymentEmailSender.ResolveSecurity(new MailOptions { Server = "localhost" }));
    }

    [Theory]
    [InlineData(MailSecurityMode.None, SecureSocketOptions.None)]
    [InlineData(MailSecurityMode.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(MailSecurityMode.SslOnConnect, SecureSocketOptions.SslOnConnect)]
    public void ExplicitModeIsUsedWithoutAutomaticDowngrade(MailSecurityMode mode, SecureSocketOptions expected)
    {
        Assert.Equal(expected, SmtpPaymentEmailSender.ResolveSecurity(new MailOptions { Server = "localhost", SecurityMode = mode }));
    }

    [Theory]
    [InlineData("", 587, MailSecurityMode.None)]
    [InlineData("localhost", 0, MailSecurityMode.None)]
    [InlineData("localhost", 65536, MailSecurityMode.StartTls)]
    [InlineData("localhost", 465, MailSecurityMode.None)]
    [InlineData("localhost", 587, (MailSecurityMode)99)]
    public void InvalidSettingsFailBeforeConnecting(string server, int port, MailSecurityMode mode)
    {
        var error = Assert.Throws<EmailDeliveryException>(() => SmtpPaymentEmailSender.ResolveSecurity(
            new MailOptions { Server = server, Port = port, SecurityMode = mode }));
        Assert.Equal("SMTP_CONFIG_MISSING", error.Code);
    }

    [Fact]
    public async Task NoneDoesNotUpgradeEvenWhenServerAdvertisesStartTls()
    {
        await using var server = new ProbeServer(advertiseTls: true);
        Assert.Equal("SMTP_PLAINTEXT_OK", await MailConnectionDiagnostics.CheckAsync(server.Options(MailSecurityMode.None), CancellationToken.None));
        Assert.DoesNotContain("STARTTLS", server.Commands);
        Assert.DoesNotContain("AUTH", server.Commands);
        Assert.DoesNotContain("MAIL", server.Commands);
    }

    [Fact]
    public async Task AuthenticationProbeDoesNotSendAnyMessage()
    {
        await using var server = new ProbeServer(advertiseTls: true);
        Assert.Equal("SMTP_PLAINTEXT_AUTH_OK", await MailConnectionDiagnostics.CheckAsync(
            server.Options(MailSecurityMode.None), CancellationToken.None, authenticate: true));
        Assert.Contains("AUTH", server.Commands);
        Assert.DoesNotContain("STARTTLS", server.Commands);
        Assert.DoesNotContain("MAIL", server.Commands);
        Assert.DoesNotContain("RCPT", server.Commands);
        Assert.DoesNotContain("DATA", server.Commands);
    }

    [Fact]
    public async Task RequiredTlsNeverAuthenticatesAgainstPlaintextOnlyServer()
    {
        await using var server = new ProbeServer(advertiseTls: false);
        Assert.Equal("SMTP_TLS_FAILED", await MailConnectionDiagnostics.CheckAsync(
            server.Options(MailSecurityMode.StartTls), CancellationToken.None, authenticate: true));
        Assert.DoesNotContain("AUTH", server.Commands);
        Assert.DoesNotContain("MAIL", server.Commands);
    }

    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        private readonly Task _server;
        public List<string> Commands { get; } = [];

        public ProbeServer(bool advertiseTls)
        {
            _listener.Start();
            _server = RunAsync(advertiseTls);
        }

        public MailOptions Options(MailSecurityMode mode) => new()
        {
            Server = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port, SecurityMode = mode,
            Username = "smtp-test-user", Password = "not-a-real-password",
        };

        private async Task RunAsync(bool advertiseTls)
        {
            using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 localhost ESMTP test");
            while (await reader.ReadLineAsync(_timeout.Token) is { } line)
            {
                var command = line.Split(' ', 2)[0];
                Commands.Add(command); // Never retain credentials, even in test probes.
                switch (command)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-localhost");
                        if (advertiseTls) await writer.WriteLineAsync("250-STARTTLS");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                        break;
                    case "AUTH": await writer.WriteLineAsync("235 2.7.0 Authenticated"); break;
                    case "QUIT": await writer.WriteLineAsync("221 Bye"); return;
                    default: await writer.WriteLineAsync("500 Unexpected command"); break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _server; }
            finally { _listener.Stop(); _timeout.Dispose(); }
        }
    }
}
