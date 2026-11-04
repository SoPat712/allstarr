using System.Data;
using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed partial class DurableJobQueue
{
    public async Task<DurableJobEnqueueResult> EnqueueManualPlaylistMaterializationAsync(
        PlaylistLinkRecord link, long generation, Guid? snapshotId = null, string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!link.Enabled || link.ImportMode != PlaylistImportMode.Linked || link.TargetProtocol != "jellyfin")
            return await EnqueueAsync(CreateManualPlaylistMaterializationRequest(link, generation, snapshotId, correlationId),
                cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var result = await EnqueueManualPlaylistMaterializationInExistingTransactionAsync(
            context, link, generation, snapshotId, correlationId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<int> EnqueueJellyfinPlaylistConsentSyncsAsync(
        Guid ownerUserId, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var candidates = await context.PlaylistLinks.AsNoTracking()
            .Where(link => link.OwnerUserId == ownerUserId && link.Enabled &&
                link.ImportMode == PlaylistImportMode.Linked && link.TargetProtocol == "jellyfin")
            .Select(link => new
            {
                Link = link,
                LastRunCode = context.PlaylistSyncRuns
                    .Where(run => run.PlaylistLinkId == link.Id && run.OwnerUserId == ownerUserId &&
                        run.State != PlaylistSyncState.Pending && run.State != PlaylistSyncState.Running &&
                        run.PlaylistSourceSnapshotId == context.PlaylistSourceSnapshots
                            .Where(snapshot => snapshot.PlaylistLinkId == link.Id &&
                                snapshot.OwnerUserId == ownerUserId && snapshot.PublishedAt.HasValue)
                            .OrderByDescending(snapshot => snapshot.SnapshotVersion)
                            .ThenByDescending(snapshot => snapshot.RetrievedAt)
                            .Select(snapshot => (Guid?)snapshot.Id).FirstOrDefault())
                    .OrderByDescending(run => run.Generation).ThenByDescending(run => run.StartedAt)
                    .ThenByDescending(run => run.Id).Select(run => run.ConflictCode).FirstOrDefault()
            }).ToArrayAsync(cancellationToken);
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.LastRunCode?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(code => code.Equals("order", StringComparison.OrdinalIgnoreCase) ||
                        code.Equals("credential_expired", StringComparison.OrdinalIgnoreCase)) != true)
                continue;
            var result = await EnqueueManualPlaylistMaterializationInExistingTransactionAsync(
                context, candidate.Link, _clock.UtcNow.UtcTicks, null, correlationId, cancellationToken);
            if (result.Created) count++;
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private async Task<DurableJobEnqueueResult> EnqueueManualPlaylistMaterializationInExistingTransactionAsync(
        AllstarrDbContext context, PlaylistLinkRecord link, long generation, Guid? snapshotId,
        string? correlationId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Playlist work requires the caller's transaction.");
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        var active = await context.Jobs.AsNoTracking().Where(item =>
            item.OwnerUserId == link.OwnerUserId && item.Type == "playlist.materialize" &&
            item.CancellationRequestedAt == null &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running ||
             item.State == DurableJobState.RetryScheduled)).ToArrayAsync(cancellationToken);
        foreach (var job in active)
        {
            try
            {
                if (JsonSerializer.Deserialize<PlaylistMaterializationJobPayload>(job.PayloadJson)?.PlaylistLinkId == link.Id)
                    return new(job.Id, false);
            }
            catch (JsonException) { }
        }
        return await EnqueueInExistingTransactionAsync(context,
            CreateManualPlaylistMaterializationRequest(link, generation, snapshotId, correlationId), cancellationToken);
    }

    private static DurableJobEnqueueRequest<PlaylistMaterializationJobPayload> CreateManualPlaylistMaterializationRequest(
        PlaylistLinkRecord link, long generation, Guid? snapshotId, string? correlationId) =>
        new("playlist.materialize", $"manual:{link.Id:N}:generation:{generation}",
            new(link.Id, generation, snapshotId), link.OwnerUserId,
            ProviderAccountId: link.ImportMode == PlaylistImportMode.Linked ? link.ProviderAccountId : null,
            Capability: link.ImportMode == PlaylistImportMode.Linked ? "playlist" : null,
            CorrelationId: correlationId);
}
