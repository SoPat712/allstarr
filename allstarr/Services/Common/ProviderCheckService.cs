using System.Threading.Channels;
using allstarr.Core.Operations;

namespace allstarr.Services.Common;

public interface IProviderCheckRunner
{
    Task RestoreAsync(CancellationToken token);
    Task<bool> SweepAsync(CancellationToken token, string? providerId = null);
}

public interface IProviderCheckRequests
{
    void Request(string providerId);
}

// Signing in or changing an account asks for that source to be checked now instead of at the next sweep.
public sealed class ProviderCheckRequests : IProviderCheckRequests
{
    private readonly Channel<string> requests = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Request(string providerId)
    {
        if (!string.IsNullOrWhiteSpace(providerId)) requests.Writer.TryWrite(providerId.Trim().ToLowerInvariant());
    }

    internal IAsyncEnumerable<string> ReadAllAsync(CancellationToken token) => requests.Reader.ReadAllAsync(token);
}

public sealed class ProviderCheckService(
    IProviderCheckRunner runner,
    ProviderCheckRequests requests,
    BackgroundTaskActivity activity,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ProviderCheckService> logger) : BackgroundService
{
    private readonly TimeSpan interval = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("ProviderChecks:IntervalHours", 6d), 1, 168));
    private readonly TimeSpan initialDelay = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("ProviderChecks:InitialDelaySeconds", 120d), 0, 3600));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await runner.RestoreAsync(cancellationToken);
        activity.Register("provider-checks", "Provider checks", interval, initialDelay: initialDelay);
        await base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunScheduleAsync(stoppingToken), RunRequestsAsync(stoppingToken));

    private async Task RunRequestsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var providerId in requests.ReadAllAsync(stoppingToken))
            {
                try { await runner.SweepAsync(stoppingToken, providerId); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception) { logger.LogWarning("Requested provider check for {Provider} could not complete", providerId); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunScheduleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(initialDelay, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                activity.Started("provider-checks");
                var success = false;
                try
                {
                    await runner.SweepAsync(stoppingToken);
                    success = true;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception) { logger.LogWarning("Provider checks could not complete; they will resume at the next interval"); }
                await activity.CompletedAsync("provider-checks", success, stoppingToken);
                await Task.Delay(interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
