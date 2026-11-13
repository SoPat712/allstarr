using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

internal static class PlaylistSnapshotRetention
{
    public static Task<int> PruneDocumentsAsync(IQueryable<ExternalMetadataSnapshotRecord> obsolete, CancellationToken token) =>
        obsolete.OrderBy(item => item.SnapshotVersion).ThenBy(item => item.Id).Take(100).ExecuteDeleteAsync(token);

    public static async Task PruneSourcesAsync(AllstarrDbContext db, Guid? linkId, CancellationToken token)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        // Reconciliation compares the current and previous publications. One-time imports keep their original source.
        var obsolete = db.PlaylistSourceSnapshots.Where(snapshot =>
            (linkId == null || snapshot.PlaylistLinkId == linkId) && snapshot.RetrievedAt < cutoff &&
            db.PlaylistLinks.Any(link => link.Id == snapshot.PlaylistLinkId && link.ImportMode != PlaylistImportMode.OneTime) &&
            db.PlaylistSourceSnapshots.Where(newer => newer.PlaylistLinkId == snapshot.PlaylistLinkId &&
                newer.PublishedAt.HasValue && newer.SnapshotVersion > snapshot.SnapshotVersion)
                .OrderByDescending(newer => newer.SnapshotVersion).Skip(1).Any() &&
            !db.PlaylistSyncRuns.Any(run => run.PlaylistSourceSnapshotId == snapshot.Id) &&
            !db.PlaylistSyncEntryResults.Any(result => db.PlaylistSourceEntries.Any(entry =>
                entry.Id == result.PlaylistSourceEntryId && entry.PlaylistSourceSnapshotId == snapshot.Id)) &&
            !db.Jobs.Any(job => job.State != DurableJobState.Succeeded && job.State != DurableJobState.Cancelled &&
                (job.Id == snapshot.SourceJobId || job.PayloadJson.ToLower().Contains(snapshot.Id.ToString().ToLower()) ||
                 job.PayloadJson.ToLower().Contains(snapshot.Id.ToString().Replace("-", "").ToLower()))));

        var candidates = await obsolete.OrderBy(item => item.RetrievedAt).ThenBy(item => item.Id)
            .Select(item => item.Id).Take(10).ToArrayAsync(token);
        if (candidates.Length == 0) return;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var ids = await obsolete.Where(item => candidates.Contains(item.Id)).Select(item => item.Id).ToArrayAsync(token);
        if (ids.Length == 0) return;
        var entries = db.PlaylistSourceEntries.Where(item => ids.Contains(item.PlaylistSourceSnapshotId));
        var entryIds = await entries.OrderBy(item => item.PlaylistSourceSnapshotId).ThenBy(item => item.SourcePosition)
            .Select(item => item.Id).Take(500).ToArrayAsync(token);
        await entries.Where(item => entryIds.Contains(item.Id)).ExecuteDeleteAsync(token);
        await obsolete.Where(item => ids.Contains(item.Id) &&
            !db.PlaylistSourceEntries.Any(entry => entry.PlaylistSourceSnapshotId == item.Id)).ExecuteDeleteAsync(token);
        // Song metadata also supplies history resolution and JSON play/report references; retain it without a complete proof.
        await transaction.CommitAsync(token);
    }
}
