using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

// Taste reads a listener's whole history whenever it changed, which is seconds for a long one. Doing that shortly after
// start and then every few minutes means a visit after a restart or after listening is as quick as the rest.
public sealed class TasteWarmUp(TasteService taste, IDbContextFactory<AllstarrDbContext> factory, DurableStorageState storage,
    ILogger<TasteWarmUp> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (storage.GetSnapshot().Readiness == DurableStorageReadiness.Ready) await WarmAsync(stoppingToken);
                await Task.Delay(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Only listeners whose plays or likes changed are read again; the rest answer from what is kept.
    private async Task WarmAsync(CancellationToken stoppingToken)
    {
        Guid[] users;
        await using (var db = await factory.CreateDbContextAsync(stoppingToken))
            users = await db.Users.AsNoTracking().Where(user => user.Enabled && !user.IsSynthetic).Select(user => user.Id).ToArrayAsync(stoppingToken);
        foreach (var user in users)
        {
            try { await taste.GetAsync(user, stoppingToken); }
            catch (Exception error) when (error is not OperationCanceledException) { logger.LogDebug("Taste warm-up skipped a listener ({ErrorType})", error.GetType().Name); }
        }
    }
}
