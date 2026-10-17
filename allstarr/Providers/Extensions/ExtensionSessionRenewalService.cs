namespace allstarr.Core.Extensions;

public sealed class ExtensionSessionRenewalService(
    ExtensionRuntimeCoordinator runtime,
    ILogger<ExtensionSessionRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunRenewalAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunRenewalAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

        async Task RunRenewalAsync(CancellationToken cancellationToken)
        {
            try { await runtime.RefreshSignedSessionsAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                logger.LogWarning("Extension session renewal is unavailable; checks will resume next tick");
            }
        }
    }
}
