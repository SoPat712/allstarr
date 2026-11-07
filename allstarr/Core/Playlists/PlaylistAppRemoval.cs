using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed class PlaylistAppRemovalException(string message) : Exception(message);
public sealed record PlaylistAppRestoreResult(PlaylistLinkRecord Link, Guid? JobId, int SkippedItemCount = 0);
public sealed record PlaylistAppRemovalSong(string BackendItemId, string Name, string? Artist);
public sealed record PlaylistAppRemovalPreview(IReadOnlyList<PlaylistAppRemovalSong> UserAddedSongs);
internal sealed record PlaylistAppSnapshotEntry(string BackendItemId, bool AddedBySync);
internal sealed record PlaylistAppSnapshot(BackendPlaylistSnapshot Playlist,
    IReadOnlyList<PlaylistAppSnapshotEntry> Entries, BackendPlaylistArtwork? Artwork, long LinkRevision);

internal static class PlaylistAppRemoval
{
    private const string ResourceKind = "playlist-app-removal";
    public static string Key(Guid id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("N")))).ToLowerInvariant();
    private static IQueryable<ExternalMetadataSnapshotRecord> History(AllstarrDbContext db) =>
        db.ExternalMetadataSnapshots.Where(item => item.ProviderId == "allstarr" && item.ResourceKind == ResourceKind);
    public static IQueryable<ExternalMetadataSnapshotRecord> Rows(AllstarrDbContext db)
    {
        var history = History(db);
        return history.Where(item => item.ProviderRevision == "removed" && !history.Any(newer =>
            newer.OwnerUserId == item.OwnerUserId && newer.ExternalIdHash == item.ExternalIdHash &&
            newer.SnapshotVersion > item.SnapshotVersion));
    }
    public static bool CanRemove(PlaylistLinkRecord link) => link.ImportMode == PlaylistImportMode.Linked &&
        !PersonalMixService.IsExternalLink(link) && link.TargetProtocol == "jellyfin" &&
        !string.IsNullOrWhiteSpace(link.TargetPlaylistId);

    public static IQueryable<PlaylistLinkRecord> SharedDestinations(AllstarrDbContext db) =>
        db.PlaylistLinks.Where(link => link.TargetPlaylistId != null && db.PlaylistLinks.Any(other =>
            other.Id != link.Id && other.TargetProtocol == link.TargetProtocol &&
            other.TargetBackendInstanceId == link.TargetBackendInstanceId && other.TargetPlaylistId == link.TargetPlaylistId));

    public static async Task<ExternalMetadataSnapshotRecord?> FindAsync(AllstarrDbContext db, PlaylistLinkRecord link,
        CancellationToken cancellationToken) => await Rows(db).SingleOrDefaultAsync(item =>
            item.OwnerUserId == link.OwnerUserId && item.ExternalIdHash == Key(link.Id), cancellationToken);

    public static async Task<int> NextVersionAsync(AllstarrDbContext db, PlaylistLinkRecord link, CancellationToken cancellationToken) =>
        (await History(db).Where(item => item.OwnerUserId == link.OwnerUserId && item.ExternalIdHash == Key(link.Id))
            .MaxAsync(item => (int?)item.SnapshotVersion, cancellationToken) ?? 0) + 1;

    public static Task<ExternalMetadataSnapshotRecord?> SavedSnapshotAsync(AllstarrDbContext db, PlaylistLinkRecord link,
        CancellationToken cancellationToken) => History(db).Where(item => item.OwnerUserId == link.OwnerUserId &&
            item.ExternalIdHash == Key(link.Id) && item.ProviderRevision == "saved")
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);

    public static ExternalMetadataSnapshotRecord Marker(PlaylistLinkRecord link, ProtocolExecutionContext execution,
        DateTimeOffset now, int version, bool removed, PlaylistAppSnapshot? snapshot = null)
    {
        var payload = snapshot == null ? JsonSerializer.Serialize(new { removed }) : JsonSerializer.Serialize(snapshot);
        return new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = link.OwnerUserId,
            Protocol = link.TargetProtocol,
            BackendInstanceId = link.TargetBackendInstanceId,
            BackendPrincipalId = execution.VerifiedBackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = ResourceKind,
            ExternalIdHash = Key(link.Id),
            SnapshotVersion = version,
            ProviderRevision = removed ? "removed" : "added",
            PayloadJson = payload,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            CorrelationId = execution.CorrelationId,
            RetrievedAt = now
        };
    }
}

public sealed partial class PlaylistOrchestrationService
{
    public async Task<PlaylistAppRemovalPreview> PreviewAppRemovalAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
        CancellationToken cancellationToken = default)
    {
        await using var writeLock = await _locks.AcquireAsync($"playlist-apps:{playlistLinkId:N}", cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await LoadOwnAppLinkAsync(db, execution, playlistLinkId, cancellationToken);
        await RequireRemovableAppLinkAsync(db, link, cancellationToken);
        var snapshot = await ReadAppSnapshotAsync(db, execution, link, false, cancellationToken);
        return new(snapshot.Playlist.Members.Where((_, index) => !snapshot.Entries[index].AddedBySync)
            .Select(member =>
            {
                if (member.NativeEntryJson == null) return new PlaylistAppRemovalSong(member.BackendItemId, "Song", null);
                using var native = JsonDocument.Parse(member.NativeEntryJson);
                var root = native.RootElement;
                var name = root.TryGetProperty("Name", out var title) ? title.GetString() ?? "Song" : "Song";
                var artist = root.TryGetProperty("Artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", artists.EnumerateArray().Select(item => item.GetString())) : null;
                return new PlaylistAppRemovalSong(member.BackendItemId, name, artist);
            }).ToArray());
    }

    public async Task<PlaylistLinkRecord> RemoveFromAppsAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        await using var writeLock = await _locks.AcquireAsync($"playlist-apps:{playlistLinkId:N}", cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await LoadOwnAppLinkAsync(db, execution, playlistLinkId, cancellationToken);
        if (link.Revision != expectedRevision) throw new DbUpdateConcurrencyException();
        if (await PlaylistAppRemoval.FindAsync(db, link, cancellationToken) != null) return link;
        await RequireRemovableAppLinkAsync(db, link, cancellationToken);
        var snapshot = await ReadAppSnapshotAsync(db, execution, link, true, cancellationToken);
        var saved = PlaylistAppRemoval.Marker(link, execution, _clock.UtcNow,
            await PlaylistAppRemoval.NextVersionAsync(db, link, cancellationToken), true, snapshot);
        saved.ProviderRevision = "saved";
        db.ExternalMetadataSnapshots.Add(saved);
        await db.SaveChangesAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Entry(link).ReloadAsync(cancellationToken);
        if (link.Revision != expectedRevision) throw new DbUpdateConcurrencyException();
        await RequireRemovableAppLinkAsync(db, link, cancellationToken);
        var targetId = link.TargetPlaylistId;
        link.Enabled = false;
        await SetAppScheduleAsync(db, link, false, cancellationToken);
        var destinationMode = link.DestinationMode;
        await PlaylistDestinationChange.ApplyAsync(db, link, null, _clock.UtcNow, cancellationToken);
        link.DestinationMode = destinationMode;
        link.TargetPlaylistId = null;
        link.UpdatedAt = _clock.UtcNow;
        link.Revision++;
        db.ExternalMetadataSnapshots.Add(PlaylistAppRemoval.Marker(link, execution, _clock.UtcNow,
            await PlaylistAppRemoval.NextVersionAsync(db, link, cancellationToken), true, snapshot));
        await RecordAppActivityAsync(db, execution, link, false, cancellationToken);
        if (targetId != null)
        {
            var target = _targets.Resolve(link.TargetProtocol);
            var context = new BackendPlaylistTargetContext(link.TargetBackendInstanceId,
                execution.VerifiedBackendPrincipalId, link.TargetCredentialReferenceId?.ToString());
            var deleted = await target.DeleteAsync(context, targetId, cancellationToken);
            if (!deleted.IsSuccess && deleted.Status != BackendPlaylistTargetStatus.NotFound)
                throw new PlaylistAppRemovalException("The Jellyfin playlist could not be removed. Try again.");
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return link;
    }

    public async Task<PlaylistAppRestoreResult> RestoreToAppsAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
        long expectedRevision, CancellationToken cancellationToken = default, bool enqueueMaterialization = true)
    {
        await using var writeLock = await _locks.AcquireAsync($"playlist-apps:{playlistLinkId:N}", cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var link = await LoadOwnAppLinkAsync(db, execution, playlistLinkId, cancellationToken);
        if (link.Revision != expectedRevision) throw new DbUpdateConcurrencyException();
        var marker = await PlaylistAppRemoval.FindAsync(db, link, cancellationToken);
        if (marker == null) return new(link, null);
        using var payload = JsonDocument.Parse(marker.PayloadJson);
        var snapshot = payload.RootElement.TryGetProperty("Playlist", out _)
            ? JsonSerializer.Deserialize<PlaylistAppSnapshot>(marker.PayloadJson) : null;
        var skippedItemCount = 0;
        string? stagedId = null;
        var committed = false;
        try
        {
            if (snapshot != null)
            {
                var target = _targets.Resolve(link.TargetProtocol);
                var context = AppTargetContext(execution, link);
                var ids = snapshot.Playlist.Members.Select(item => item.BackendItemId).ToArray();
                var availableIds = new HashSet<string>(StringComparer.Ordinal);
                if (ids.Length > 0)
                {
                    var available = await target.ReadItemsAsync(context, ids, cancellationToken);
                    if (!available.IsSuccess || available.Value == null)
                        throw new PlaylistAppRemovalException("The saved Jellyfin songs could not be checked. Try again.");
                    availableIds.UnionWith(available.Value.Select(item => item.BackendItemId));
                }
                var restoredIds = ids.Where(availableIds.Contains).ToArray();
                skippedItemCount = ids.Length - restoredIds.Length;
                var created = await target.WriteAsync(context, new(BackendPlaylistWriteMode.Reconcile,
                    new(snapshot.Playlist.Name, snapshot.Playlist.Description, snapshot.Artwork?.Bytes, snapshot.Artwork?.ContentType),
                    restoredIds, $"playlist-restore:{link.Id:N}:{marker.SnapshotVersion}", preserveDuplicateItems: true), cancellationToken);
                stagedId = created.Value?.Snapshot.BackendPlaylistId ?? created.RecoveryPlaylistId;
                if (!created.IsSuccess || created.Value == null)
                    throw new PlaylistAppRemovalException("The Jellyfin playlist could not be restored. Try again.");
                if (!created.Value.Snapshot.Members.Select(item => item.BackendItemId).SequenceEqual(restoredIds))
                    throw new PlaylistAppRemovalException("Jellyfin did not keep the saved playlist order. Try again.");
                link.TargetPlaylistId = stagedId;
                var syncIds = snapshot.Entries.Where(item => item.AddedBySync && availableIds.Contains(item.BackendItemId))
                    .Select(item => item.BackendItemId).ToArray();
                var memberships = await db.PlaylistTargetMemberships.Where(item => item.PlaylistLinkId == link.Id &&
                    syncIds.Contains(item.TargetEntryId)).ToListAsync(cancellationToken);
                foreach (var membership in memberships)
                {
                    membership.Active = true;
                    membership.RemovalConfirmedAt = null;
                    membership.LastKnownPosition = Array.IndexOf(restoredIds, membership.TargetEntryId);
                    membership.UpdatedAt = _clock.UtcNow;
                    membership.Revision++;
                }
            }
            link.Enabled = true;
            link.UpdatedAt = _clock.UtcNow;
            link.Revision++;
            await SetAppScheduleAsync(db, link, true, cancellationToken);
            db.ExternalMetadataSnapshots.Add(PlaylistAppRemoval.Marker(link, execution, _clock.UtcNow,
                marker.SnapshotVersion + 1, false));
            await RecordAppActivityAsync(db, execution, link, true, cancellationToken);
            Guid? jobId = null;
            if (snapshot == null && enqueueMaterialization)
            {
                var snapshotId = await db.PlaylistSourceSnapshots.Where(item => item.PlaylistLinkId == link.Id &&
                    item.OwnerUserId == link.OwnerUserId && item.PublishedAt.HasValue)
                    .OrderByDescending(item => item.SnapshotVersion).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
                var lastGeneration = await db.PlaylistSyncRuns.Where(item => item.PlaylistLinkId == link.Id)
                    .Select(item => (long?)item.Generation).MaxAsync(cancellationToken) ?? 0;
                var generation = Math.Max(_clock.UtcNow.UtcTicks, lastGeneration + 1);
                var job = await _jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
                    "playlist.materialize", $"playlist-readd:{link.Id:N}:{link.Revision}",
                    new(link.Id, generation, snapshotId), link.OwnerUserId,
                    ProviderAccountId: link.ProviderAccountId, Capability: "playlist", CorrelationId: execution.CorrelationId), cancellationToken);
                jobId = job.JobId;
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return new(link, jobId, skippedItemCount);
        }
        finally
        {
            if (!committed && stagedId != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _targets.Resolve(link.TargetProtocol).DeleteAsync(AppTargetContext(execution, link), stagedId, cleanup.Token);
            }
        }
    }

    private static BackendPlaylistTargetContext AppTargetContext(ProtocolExecutionContext execution, PlaylistLinkRecord link) =>
        new(link.TargetBackendInstanceId, execution.VerifiedBackendPrincipalId, link.TargetCredentialReferenceId?.ToString())
        { UseConfiguredAuthentication = true };

    private static async Task RequireRemovableAppLinkAsync(AllstarrDbContext db, PlaylistLinkRecord link, CancellationToken cancellationToken)
    {
        if (!PlaylistAppRemoval.CanRemove(link))
            throw new InvalidOperationException("Only playlists kept in sync with Jellyfin can be removed from your apps here.");
        if (await PlaylistAppRemoval.SharedDestinations(db).AnyAsync(item => item.Id == link.Id, cancellationToken))
            throw new InvalidOperationException("Another listener uses this app playlist, so it cannot be removed here.");
    }

    private async Task<PlaylistAppSnapshot> ReadAppSnapshotAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        PlaylistLinkRecord link, bool includeArtwork, CancellationToken cancellationToken)
    {
        var target = _targets.Resolve(link.TargetProtocol);
        var context = AppTargetContext(execution, link);
        var read = await target.ReadAsync(context, link.TargetPlaylistId!, cancellationToken);
        if (includeArtwork && read.Status == BackendPlaylistTargetStatus.NotFound)
        {
            var saved = await PlaylistAppRemoval.SavedSnapshotAsync(db, link, cancellationToken);
            if (saved != null && JsonSerializer.Deserialize<PlaylistAppSnapshot>(saved.PayloadJson) is { } prior &&
                prior.Playlist.BackendPlaylistId == link.TargetPlaylistId && prior.LinkRevision == link.Revision) return prior;
        }
        if (!read.IsSuccess || read.Value == null || read.Value.BackendPlaylistId != link.TargetPlaylistId)
            throw new PlaylistAppRemovalException("The Jellyfin playlist could not be saved before removal. Try again.");
        var syncIds = (await db.PlaylistTargetMemberships.Where(item => item.PlaylistLinkId == link.Id &&
                (item.Active || item.RemovalConfirmedAt == null))
            .Select(item => item.TargetEntryId).ToArrayAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var entries = read.Value.Members.Select(item => new PlaylistAppSnapshotEntry(item.BackendItemId, syncIds.Remove(item.BackendItemId))).ToArray();
        BackendPlaylistArtwork? artwork = null;
        if (includeArtwork && (target.Capabilities.CanWriteArtwork || read.Value.ArtworkReference != null))
        {
            var image = await target.ReadArtworkAsync(context, link.TargetPlaylistId!, read.Value.ArtworkReference, cancellationToken);
            if (image.IsSuccess) artwork = image.Value;
            else if (image.Status != BackendPlaylistTargetStatus.NotFound || read.Value.ArtworkReference != null)
                throw new PlaylistAppRemovalException("The Jellyfin artwork could not be saved before removal. Try again.");
        }
        return new(read.Value, entries, artwork, link.Revision);
    }

    private static async Task<PlaylistLinkRecord> LoadOwnAppLinkAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        Guid id, CancellationToken cancellationToken)
    {
        var link = await db.PlaylistLinks.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Playlist link not found.");
        if (execution.Principal?.UserId != link.OwnerUserId || execution.Actor?.EffectiveUserId != link.OwnerUserId)
            throw new UnauthorizedAccessException();
        RequireLinkExecutionContext(execution, execution.RequireActor(), link);
        if (!await db.Users.AnyAsync(item => item.Id == link.OwnerUserId && item.Enabled &&
                item.BackendType == link.TargetProtocol && item.BackendInstanceId == link.TargetBackendInstanceId &&
                item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, cancellationToken))
            throw new UnauthorizedAccessException();
        return link;
    }

    private async Task SetAppScheduleAsync(AllstarrDbContext db, PlaylistLinkRecord link, bool enabled, CancellationToken cancellationToken)
    {
        if (link.ScheduleId is not { } scheduleId) return;
        var schedule = await db.JobSchedules.SingleAsync(item => item.Id == scheduleId &&
            item.OwnerUserId == link.OwnerUserId && item.JobType == DurableScheduleEngine.PlaylistSyncJobType, cancellationToken);
        schedule.Enabled = enabled;
        schedule.NextRunAt = enabled ? DurableScheduleEngine.GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, _clock.UtcNow) : null;
        schedule.UpdatedAt = _clock.UtcNow;
        schedule.Revision++;
    }

    private async Task RecordAppActivityAsync(AllstarrDbContext db, ProtocolExecutionContext execution, PlaylistLinkRecord link,
        bool added, CancellationToken cancellationToken)
    {
        var name = await db.PlaylistSourceSnapshots.Where(item => item.PlaylistLinkId == link.Id)
            .OrderByDescending(item => item.SnapshotVersion).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken) ?? "Playlist";
        db.AuditEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = link.OwnerUserId,
            Category = "playlist-link",
            Action = added ? "added-to-apps" : "removed-from-apps",
            Outcome = "success",
            SubjectType = "playlist-link",
            SubjectId = link.Id.ToString(),
            CorrelationId = execution.CorrelationId,
            CreatedAt = _clock.UtcNow,
            DetailsJson = JsonSerializer.Serialize(new
            {
                playlistLinkId = link.Id,
                playlistName = name,
                message = added ? $"{name} was added back to your apps using its saved matches."
                    : $"{name} was removed from your apps. Its source and saved matches were kept."
            })
        });
    }
}
