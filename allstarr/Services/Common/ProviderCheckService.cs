using allstarr.Core.Operations;

namespace allstarr.Services.Common;

public interface IProviderCheckRunner
{
    Task RestoreAsync(CancellationToken token);
    Task<bool> SweepAsync(CancellationToken token);
}

public sealed class ProviderCheckService(
    IProviderCheckRunner runner,
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
