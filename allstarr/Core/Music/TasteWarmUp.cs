using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

// Taste reads a listener's whole history the first time, which is seconds for a long one. Doing that shortly after
// start means the first visit after a restart is as quick as the rest.
public sealed class TasteWarmUp(TasteService taste, IDbContextFactory<AllstarrDbContext> factory, DurableStorageState storage,
    ILogger<TasteWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready) return;
            Guid[] users;
            await using (var db = await factory.CreateDbContextAsync(stoppingToken))
                users = await db.Users.AsNoTracking().Where(user => user.Enabled && !user.IsSynthetic).Select(user => user.Id).ToArrayAsync(stoppingToken);
            foreach (var user in users)
            {
                try { await taste.GetAsync(user, stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException) { logger.LogDebug("Taste warm-up skipped a listener ({ErrorType})", error.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
