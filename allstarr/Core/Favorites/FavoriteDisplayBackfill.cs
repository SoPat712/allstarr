using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed record FavoriteDisplayBackfillPayload(Guid StateId, Guid EventId);

public sealed class FavoriteDisplayBackfillQueue(
    IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs, IPlatformClock clock)
{
    public const string JobType = "favorite.display-backfill";
    private const int MaximumOutstanding = 100;

    public async Task<int> EnqueuePendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var outstanding = await db.Jobs.CountAsync(item => item.Type == JobType &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running ||
             item.State == DurableJobState.RetryScheduled), cancellationToken);
        if (outstanding >= MaximumOutstanding) return 0;
        // Exhausted or unavailable favorites may be attempted again the following week.
        var week = clock.UtcNow.ToUnixTimeSeconds() / (7 * 24 * 60 * 60);
        var prefix = $"favorite-display:{week}:";
        var pending = await Missing(db).AsNoTracking().Where(state => !db.Jobs.Any(job =>
                job.Type == JobType && job.OwnerUserId == state.OwnerUserId &&
                job.IdempotencyKey == prefix + state.LastEventId.ToString().ToLower()))
            .OrderBy(state => state.UpdatedAt).ThenBy(state => state.Id)
            .Take(MaximumOutstanding - outstanding)
            .Select(state => new { state.Id, state.LastEventId, state.OwnerUserId }).ToArrayAsync(cancellationToken);
        var count = 0;
        foreach (var state in pending)
        {
            var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<FavoriteDisplayBackfillPayload>(JobType,
                prefix + state.LastEventId.ToString("D"), new(state.Id, state.LastEventId), state.OwnerUserId,
                Priority: -100, MaxAttempts: 3, CorrelationId: "favorite-display-backfill"), cancellationToken);
            if (result.Created) count++;
        }
        return count;
    }

    internal static IQueryable<FavoriteStateRecord> Missing(AllstarrDbContext db) =>
        db.Set<FavoriteStateRecord>().Where(state => state.IsFavorite && state.DisplayMetadataJson == null &&
            state.ItemId.StartsWith("ext-") && db.Set<FavoriteEventRecord>().Any(accepted =>
                accepted.Id == state.LastEventId && accepted.OwnerUserId == state.OwnerUserId &&
                accepted.Protocol == state.Protocol && accepted.BackendInstanceId == state.BackendInstanceId &&
                accepted.ItemId == state.ItemId && accepted.Operation == FavoriteOperation.Favorite &&
                db.Users.Any(user => user.Id == state.OwnerUserId && user.Enabled &&
                    user.BackendType == state.Protocol && user.BackendInstanceId == state.BackendInstanceId &&
                    user.BackendPrincipalId == accepted.BackendPrincipalId)));
}

public sealed class FavoriteDisplayBackfillJobHandler(
    IDbContextFactory<AllstarrDbContext> factory, FavoriteActionPipeline pipeline, IPlatformClock clock,
    TimeProvider? timeProvider = null) : IDurableJobHandler, IDisposable
{
    private readonly TimeProvider lookupClock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim lookupGate = new(1, 1);
    private long lastLookup;
    private bool hasLookup;
    public string JobType => FavoriteDisplayBackfillQueue.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        var claim = context.Claim;
        FavoriteDisplayBackfillPayload? payload;
        try { payload = claim.Payload.Deserialize<FavoriteDisplayBackfillPayload>(); }
        catch (JsonException) { payload = null; }
        if (claim.Type != JobType || claim.OwnerUserId == null || payload == null ||
            payload.StateId == Guid.Empty || payload.EventId == Guid.Empty)
            return DurableJobCompletion.Failure("favorite_display_invalid", "The saved favorite display request is invalid.");

        FavoriteStateRecord? state;
        string? display;
        await lookupGate.WaitAsync(cancellationToken);
        try
        {
            if (hasLookup)
            {
                var remaining = TimeSpan.FromSeconds(1) - lookupClock.GetElapsedTime(lastLookup);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, lookupClock, cancellationToken);
            }
            await using var read = await factory.CreateDbContextAsync(cancellationToken);
            state = await FavoriteDisplayBackfillQueue.Missing(read).AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == payload.StateId && item.LastEventId == payload.EventId && item.OwnerUserId == claim.OwnerUserId,
                cancellationToken);
            if (state == null || !await IsCurrentJobAsync(read, claim, cancellationToken))
                return DurableJobCompletion.Cancelled();
            if (FavoriteDisplaySnapshot.Resource(state.ItemId) == null) return DurableJobCompletion.Success();
            var accepted = await read.Set<FavoriteEventRecord>().AsNoTracking().SingleAsync(item => item.Id == payload.EventId,
                cancellationToken);
            var execution = new ProtocolExecutionContext(state.Protocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
                state.BackendInstanceId, accepted.BackendPrincipalId,
                new(state.OwnerUserId, state.Protocol, state.BackendInstanceId, accepted.BackendPrincipalId, "Listener", false),
                claim.CorrelationId, clock.UtcNow.AddSeconds(2), cancellationToken);
            try
            {
                display = await pipeline.CaptureDisplayAsync(new(execution, state.ItemId, FavoriteOperation.Favorite,
                    "display-backfill"), state.ItemId, state.OwnerUserId, cancellationToken);
            }
            catch (UnauthorizedAccessException) { return DurableJobCompletion.Cancelled(); }
            finally
            {
                lastLookup = lookupClock.GetTimestamp();
                hasLookup = true;
            }
        }
        finally { lookupGate.Release(); }
        if (display == null)
            return DurableJobCompletion.Retry("favorite_display_unavailable", "Favorite details are unavailable and will retry.",
                TimeSpan.FromMinutes(15));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await FavoriteDisplayBackfillQueue.Missing(db).SingleOrDefaultAsync(item => item.Id == state.Id &&
            item.LastEventId == payload.EventId && item.OwnerUserId == claim.OwnerUserId,
            cancellationToken);
        if (current == null || !await IsCurrentJobAsync(db, claim, cancellationToken))
            return DurableJobCompletion.Cancelled();
        current.DisplayMetadataJson = display;
        current.UpdatedAt = clock.UtcNow;
        current.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DurableJobCompletion.Success();
    }

    private static Task<bool> IsCurrentJobAsync(AllstarrDbContext db, DurableJobClaim claim, CancellationToken cancellationToken) =>
        db.Jobs.AnyAsync(item => item.Id == claim.JobId && item.Type == FavoriteDisplayBackfillQueue.JobType &&
            item.OwnerUserId == claim.OwnerUserId && item.State == DurableJobState.Running &&
            item.AttemptCount == claim.AttemptNumber && item.CancellationRequestedAt == null, cancellationToken);

    public void Dispose() => lookupGate.Dispose();
}

public sealed class FavoriteDisplayBackfillService(FavoriteDisplayBackfillQueue queue, DurableStorageState storage,
    ILogger<FavoriteDisplayBackfillService> logger, BackgroundTaskActivity? taskActivity = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        taskActivity?.Register("favorite-display-backfill", "Saved favorite details", TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            taskActivity?.Started("favorite-display-backfill");
            try
            {
                await queue.EnqueuePendingAsync(stoppingToken);
                if (taskActivity != null) await taskActivity.CompletedAsync("favorite-display-backfill", true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Favorite display recovery discovery failed ({ErrorType})", error.GetType().Name);
                if (taskActivity != null) await taskActivity.CompletedAsync("favorite-display-backfill", false, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
