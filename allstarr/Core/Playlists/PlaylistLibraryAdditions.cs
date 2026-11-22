using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Enrichment;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record PlaylistLibrarySong(string Title, string Artist, string? Album, string ItemId);
public sealed record PlaylistLibraryPlan(IReadOnlyList<PlaylistLibrarySong> Songs, int Total, int Skipped);
public sealed record PlaylistLibraryQueueResult(int Queued, int AlreadyThere, int Skipped, Guid? JobId);
public sealed record PlaylistLibraryAdditionsPayload(Guid PlaylistLinkId, IReadOnlyList<string> ItemIds);

// Adds a playlist's streaming-only songs to the listener's library through the single-song add, one song at a time:
// one job walks the list and starts the next song only after the previous one has finished.
public sealed class PlaylistLibraryAdditions : IDurableJobHandler
{
    public const string Type = "playlist.library-additions";
    public const int Limit = 500;
    private const string CheckpointKind = "playlist-library-additions-checkpoint";
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private sealed record Checkpoint(int Next, Guid? Current);
    private readonly IDbContextFactory<AllstarrDbContext> factory;
    private readonly DurableJobQueue jobs;
    private readonly IPlatformClock clock;
    private readonly IBackendLibraryAccessResolver access;
    private readonly Func<ProtocolExecutionContext, AddClientLibraryItem, string, CancellationToken, Task<ClientLibraryAddition>> add;

    public PlaylistLibraryAdditions(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs, IPlatformClock clock,
        IBackendLibraryAccessResolver access, ClientLibraryAdditions additions)
        : this(factory, jobs, clock, access, (context, item, key, token) => additions.AddAsync(context, item, token, requestKey: key)) { }

    internal PlaylistLibraryAdditions(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs, IPlatformClock clock,
        IBackendLibraryAccessResolver access,
        Func<ProtocolExecutionContext, AddClientLibraryItem, string, CancellationToken, Task<ClientLibraryAddition>> add) =>
        (this.factory, this.jobs, this.clock, this.access, this.add) = (factory, jobs, clock, access, add);

    public string JobType => Type;

    public static PlaylistLibraryPlan Plan(DurablePlaylistProjection projection)
    {
        var songs = new List<PlaylistLibrarySong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var entry in projection.Entries.OrderBy(item => item.Position))
        {
            if (entry.RouteKind == "local") continue;
            if (entry.RouteKind != "external" || string.IsNullOrWhiteSpace(entry.RouteProviderId) || entry.BackendItemId != null)
            {
                skipped++;
                continue;
            }
            var itemId = $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}";
            if (seen.Add(itemId)) songs.Add(new(entry.Title, string.Join(", ", entry.Artists), entry.Album, itemId));
        }
        return new(songs.Take(Limit).ToArray(), songs.Count, skipped);
    }

    // One key per playlist and song, so asking again replays the earlier add instead of starting another.
    public static string RequestKey(Guid playlistLinkId, string itemId) =>
        $"playlist-add:{playlistLinkId:N}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(itemId)))[..32]}";

    public async Task<PlaylistLibraryQueueResult> QueueAsync(Guid owner, Guid playlistLinkId, PlaylistLibraryPlan plan,
        string? correlationId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var keys = plan.Songs.Select(song => RequestKey(playlistLinkId, song.ItemId)).ToArray();
            var prefix = $"playlist-add:{playlistLinkId:N}:";
            var started = (await db.Set<PromotionRunRecord>().AsNoTracking().Where(item => item.OwnerUserId == owner &&
                keys.Contains(item.RequestKey)).Select(item => item.RequestKey).ToArrayAsync(token)).ToHashSet(StringComparer.Ordinal);
            foreach (var json in await db.PromotionRequests.AsNoTracking().Where(item => item.RequesterUserId == owner &&
                         item.RequestJson.Contains(prefix)).Select(item => item.RequestJson).ToArrayAsync(token))
                started.Add(JsonSerializer.Deserialize<PromoteSongRequest>(json)!.RequestKey);
            var waiting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var json in await db.Jobs.AsNoTracking().Where(item => item.Type == Type && item.OwnerUserId == owner &&
                         (item.State == DurableJobState.Pending || item.State == DurableJobState.Running ||
                          item.State == DurableJobState.RetryScheduled)).Select(item => item.PayloadJson).ToArrayAsync(token))
                if (JsonSerializer.Deserialize<PlaylistLibraryAdditionsPayload>(json) is { } payload && payload.PlaylistLinkId == playlistLinkId)
                    waiting.UnionWith(payload.ItemIds);
            var fresh = plan.Songs.Where((song, index) => !started.Contains(keys[index]) && !waiting.Contains(song.ItemId))
                .Select(song => song.ItemId).ToArray();
            if (fresh.Length == 0) return new(0, plan.Songs.Count, plan.Skipped, null);
            var queued = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistLibraryAdditionsPayload>(Type,
                $"playlist-library-additions:{playlistLinkId:N}:{clock.UtcNow.UtcTicks}", new(playlistLinkId, fresh), owner,
                Priority: -10, CorrelationId: correlationId), token);
            return new(fresh.Length, plan.Songs.Count - fresh.Length, plan.Skipped, queued.JobId);
        }
        finally { Gate.Release(); }
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        PlaylistLibraryAdditionsPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<PlaylistLibraryAdditionsPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null || payload.ItemIds == null || execution.Claim.OwnerUserId is not { } owner)
            return DurableJobCompletion.Failure("playlist_library_additions_invalid", "The saved playlist additions are invalid.");
        await using var db = await factory.CreateDbContextAsync(token);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(execution.Claim.JobId.ToString("N"))));
        var previous = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner &&
                item.ProviderId == "allstarr" && item.ResourceKind == CheckpointKind && item.ExternalIdHash == key)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(token);
        var checkpoint = previous == null ? new Checkpoint(0, null) : JsonSerializer.Deserialize<Checkpoint>(previous.PayloadJson)!;
        if (checkpoint.Current is { } current && await StillAddingAsync(db, owner, current, token))
            return DurableJobCompletion.Continue(Wait);
        if (checkpoint.Next >= payload.ItemIds.Count) return DurableJobCompletion.Success();
        var resolved = await access.ResolveUserAsync(owner, token);
        if (resolved.Context is not { } context)
            return DurableJobCompletion.Defer("playlist_library_additions_backend", "The media server is unavailable.", TimeSpan.FromMinutes(1));
        var itemId = payload.ItemIds[checkpoint.Next];
        await execution.ReportProgressAsync(new("playlist.library-additions",
            $"Adding song {checkpoint.Next + 1} of {payload.ItemIds.Count} to the library.", checkpoint.Next, payload.ItemIds.Count), token);
        Guid? started = null;
        try
        {
            var addition = await add(context, new(itemId, "song"), RequestKey(payload.PlaylistLinkId, itemId), token);
            if (addition.State == "adding") started = addition.Id;
        }
        // A song that cannot be added any more is passed over; the rest of the list still goes ahead.
        catch (Exception exception) when (exception is KeyNotFoundException or PromotionException or ArgumentException or UnauthorizedAccessException) { }
        var json = JsonSerializer.Serialize(new Checkpoint(checkpoint.Next + 1, started));
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            SourceJobId = execution.Claim.JobId,
            Protocol = context.Protocol.ToString().ToLowerInvariant(),
            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = CheckpointKind,
            ExternalIdHash = key,
            SnapshotVersion = (previous?.SnapshotVersion ?? 0) + 1,
            ProviderRevision = "checkpoint",
            PayloadJson = json,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            CorrelationId = execution.Claim.CorrelationId,
            RetrievedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(token);
        return DurableJobCompletion.Continue(Pause);
    }

    // An add waiting for household approval does not hold up the list; one that is fetching or placing files does.
    private static async Task<bool> StillAddingAsync(AllstarrDbContext db, Guid owner, Guid id, CancellationToken token)
    {
        var request = await db.PromotionRequests.AsNoTracking().Where(item => item.Id == id && item.RequesterUserId == owner)
            .Select(item => new { item.RunId }).SingleOrDefaultAsync(token);
        if (request is { RunId: null }) return false;
        var runId = request?.RunId ?? id;
        var run = await db.Set<PromotionRunRecord>().AsNoTracking().Where(item => item.Id == runId)
            .Select(item => new { item.Outcome, item.JobId }).SingleOrDefaultAsync(token);
        if (run == null || run.Outcome is not ("Queued" or "Running")) return false;
        return await db.Jobs.AsNoTracking().AnyAsync(item => item.Id == run.JobId && (item.State == DurableJobState.Pending ||
            item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled), token);
    }
}
