using allstarr.Core.Caching;
using allstarr.Core.ClientApi;
using allstarr.Core.Extensions;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed class TasteWarmUp(TasteService taste, IDbContextFactory<AllstarrDbContext> factory, DurableStorageState storage,
    ILogger<TasteWarmUp> logger, SoundProfileService sound, ListeningStatsService stats, PersonalMixService mixes,
    IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ExtensionRuntimeCoordinator extensions,
    IPlatformClock clock, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WarmAfterStartupAsync(() => extensions.Restored, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Every, time, stoppingToken);
                await WarmAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task WarmAfterStartupAsync(Func<Task> restored, CancellationToken token)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(token);
        await restored().WaitAsync(token);
        await Task.Delay(TimeSpan.FromSeconds(1), time, token);
        await WarmAsync(token);
    }

    private async Task WarmAsync(CancellationToken token)
    {
        if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready) return;
        UserRecord[] users;
        try
        {
            var since = clock.UtcNow.AddDays(-14);
            await using var db = await factory.CreateDbContextAsync(token);
            users = await db.Users.AsNoTracking().Where(user => user.Enabled && !user.IsSynthetic && user.LastSeenAt >= since &&
                (user.BackendType == "jellyfin" || user.BackendType == "subsonic"))
                .OrderByDescending(user => user.LastSeenAt).ToArrayAsync(token);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        { logger.LogDebug("Listener cache warm-up skipped a pass ({ErrorType})", error.GetType().Name); return; }
        using (var diskScope = scopes.CreateScope())
            if (diskScope.ServiceProvider.GetService<HybridApplicationCache>() is { } cache)
                await cache.WarmRecentAsync(users.Select(user => user.Id).ToHashSet(), token);
        foreach (var user in users)
        {
            var execution = new ProtocolExecutionContext(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
                user.BackendPrincipalId, new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId,
                    user.BackendPrincipalId, user.DisplayName, user.IsAdmin), "cache-warm-up", clock.UtcNow.AddMinutes(5), token)
            { IsReadOnly = true, UseStoredBackendCredentials = true };
            await ReadAsync(async () =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SavedLibraryReader>().WarmSearchAsync(execution, token);
            });
            await ReadAsync(async () =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ILibraryIndexService>().WarmOwnershipAsync(execution, token);
            });
            await ReadAsync(() => sound.WarmAsync(user.Id, token));
            foreach (var period in new[] { "30d", "7d", "year", "all" })
                await ReadAsync(() => stats.GetAsync(execution, period, null, token));
            await ReadAsync(() => taste.WarmAsync(user.Id, token));
            await ReadAsync(async () =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<LyricsSearchReader>().WarmAsync(execution);
            });
            await ReadAsync(() => mixes.WarmCardsAsync(user.Id, token));
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, token);
        }

        async Task ReadAsync(Func<Task> read)
        {
            token.ThrowIfCancellationRequested();
            try { await read(); }
            catch (Exception error) when (!token.IsCancellationRequested)
            { logger.LogDebug("Listener cache warm-up skipped a read ({ErrorType})", error.GetType().Name); }
            // Yield between reads as well as listeners so startup does not create a burst of database work.
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, token);
        }
    }
}
