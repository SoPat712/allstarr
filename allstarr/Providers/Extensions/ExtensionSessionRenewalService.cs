namespace allstarr.Core.Extensions;

public sealed class ExtensionSessionRenewalService(
    ExtensionRuntimeCoordinator runtime,
    ILogger<ExtensionSessionRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await runtime.RefreshSignedSessionsAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    logger.LogWarning("Extension session renewal is unavailable; checks will resume next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
