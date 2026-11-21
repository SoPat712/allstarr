using System.Text.Json;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

/// <summary>
/// How fast each source starts a song is learned in memory, and the server restarts every night. Real starts and source
/// checks already keep each start they learned from, so once storage is ready the last half day is read back once and the
/// first plays after a restart choose their source as they would have before it.
/// </summary>
public sealed class PlaybackStartMemoryRestore(IDbContextFactory<AllstarrDbContext> factory, ProviderRuntimeHealth health,
    DurableStorageState storage, TimeProvider time, ILogger<PlaybackStartMemoryRestore> logger) : BackgroundService
{
    internal const int MaximumRows = 3000;
    internal const string CheckCategory = "provider-cts";
    private const int MaximumDetailsLength = 131072;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
                await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
            var restored = await RestoreAsync(stoppingToken);
            logger.LogInformation("Playback start memory restored {Samples} recent starts", restored);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { logger.LogWarning("Playback start memory could not be restored ({ErrorType})", error.GetType().Name); }
    }

    internal async Task<int> RestoreAsync(CancellationToken token)
    {
        var cutoff = time.GetUtcNow() - ProviderRuntimeHealth.PlaybackStartMemory;
        List<string> rows;
        await using (var db = await factory.CreateDbContextAsync(token))
            rows = await db.AuditEvents.AsNoTracking()
                .Where(row => (row.Category == PlaybackStartTimingStore.Category && row.SubjectType == PlaybackStartTimingStore.Category &&
                    row.SubjectId == "recent" || row.Category == CheckCategory) && row.CreatedAt >= cutoff)
                .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).Take(MaximumRows)
                .Select(row => row.DetailsJson).ToListAsync(token);
        var starts = new List<PlaybackSourceStart>();
        foreach (var json in rows)
        {
            if (json is not { Length: > 0 and <= MaximumDetailsLength }) continue;
            try
            {
                if (JsonSerializer.Deserialize<RecordedStarts>(json) is not { } recorded) continue;
                starts.AddRange((recorded.Starts ?? []).Take(PlaybackSourceStart.MaximumPerRequest).OfType<PlaybackSourceStart>());
                if (recorded.PlaybackStart is { } checkStart) starts.Add(checkStart);
            }
            // A row that no longer reads, or whose format facts fail their own checks, is skipped.
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        return health.RestorePlaybackStarts(starts.OrderBy(start => start.At));
    }

    // A real start's record lists its starts; a source check's result holds the one start it made.
    private sealed record RecordedStarts(IReadOnlyList<PlaybackSourceStart?>? Starts, PlaybackSourceStart? PlaybackStart);
}
