using allstarr.Core.Storage;

namespace allstarr.Core.Playback;

// A recap reads the listener's whole history whenever their plays changed. Reading the ones opened lately again every
// minute means a visit while or after listening is as quick as the rest.
public sealed class ListeningStatsWarmUp(ListeningStatsService stats, DurableStorageState storage, ILogger<ListeningStatsWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ListeningStatsService.WarmEvery, stoppingToken);
                if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready) continue;
                try { await stats.WarmAsync(stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException) { logger.LogDebug("Recap warm-up skipped a pass ({ErrorType})", error.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
