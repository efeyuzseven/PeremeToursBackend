using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeremeTours.Application.Authentication;

namespace PeremeTours.Infrastructure.Authentication;

internal sealed class PasswordResetRequestBuffer : IPasswordResetRequests
{
    private readonly Channel<PasswordResetRequest> _channel = Channel.CreateBounded<PasswordResetRequest>(
        new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public bool TryEnqueue(PasswordResetRequest request) => _channel.Writer.TryWrite(request);
    public IAsyncEnumerable<PasswordResetRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

internal sealed class PasswordResetWorker(PasswordResetRequestBuffer queue, IServiceScopeFactory scopes,
    ILogger<PasswordResetWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(3301, nameof(LogFailed)), "Password reset email could not be delivered. No credentials or reset tokens were logged.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    await scope.ServiceProvider.GetRequiredService<PasswordResetService>().RequestAsync(request, timeout.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch { LogFailed(logger, null); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
