using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed class PlaylistRebuildException(string message) : Exception(message);

public sealed partial class PlaylistOrchestrationService
{
    public async Task<PlaylistLinkRecord> RebuildAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
        CancellationToken cancellationToken = default)
    {
        await using var rebuildLock = await _locks.AcquireAsync($"playlist-apps:{playlistLinkId:N}", cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == playlistLinkId, cancellationToken)
            ?? throw new KeyNotFoundException("Playlist link not found.");
        if (execution.Principal == null || execution.Principal.UserId != link.OwnerUserId && !execution.Principal.IsAdministrator)
            throw new UnauthorizedAccessException();
        if (await PlaylistAppRemoval.FindAsync(db, link, cancellationToken) != null)
            throw new InvalidOperationException("Add this playlist back to your apps before rebuilding it.");
        if (link.ImportMode == PlaylistImportMode.OneTime) throw new InvalidOperationException("Imports keep your own order.");
        if (link.TargetProtocol != "jellyfin" || link.Mode == PlaylistLinkMode.Virtual || link.TargetPlaylistId == null)
            throw new InvalidOperationException("Rebuild requires a mapped Jellyfin playlist.");
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == link.OwnerUserId && item.Enabled &&
            item.BackendType == "jellyfin" && item.BackendInstanceId == link.TargetBackendInstanceId, cancellationToken)
            ?? throw new UnauthorizedAccessException();
        var ownerExecution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, owner.BackendInstanceId, owner.BackendPrincipalId,
            new(owner.Id, owner.BackendType, owner.BackendInstanceId, owner.BackendPrincipalId, owner.DisplayName, owner.IsAdmin),
            execution.CorrelationId, execution.Deadline, cancellationToken);
        var projection = await new DurablePlaylistProjectionReader(_factory, _libraryAccess)
            .ReadByLinkIdAsync(owner.Id, link.Id, cancellationToken)
            ?? throw new InvalidOperationException("Synchronize this playlist before rebuilding it.");
        var ids = projection.Entries.Where(item => item.RouteKind == "local" && item.BackendItemId != null)
            .OrderBy(item => item.Position).Select(item => item.BackendItemId!).Distinct(StringComparer.Ordinal).ToArray();
        var target = _targets.Resolve("jellyfin");
        var context = new BackendPlaylistTargetContext(owner.BackendInstanceId, owner.BackendPrincipalId)
        { UseConfiguredAuthentication = true };
        var old = await target.ReadAsync(context, link.TargetPlaylistId, cancellationToken);
        if (!old.IsSuccess || old.Value == null) throw new PlaylistRebuildException("The Jellyfin playlist could not be read.");
        BackendPlaylistArtwork? artwork = null;
        if (link.SyncArtwork && projection.ArtworkReferenceKey != null)
        {
            var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking().SingleAsync(item => item.Id == projection.SnapshotId, cancellationToken);
            var sourceArtwork = await _source.ResolveArtworkAsync(ownerExecution, link, new(new ProviderArtworkReference(
                new(link.SourceProviderId, ProviderResourceKind.Playlist, link.SourcePlaylistId), revision: snapshot.ProviderRevision)), cancellationToken);
            if (!sourceArtwork.IsSuccess) throw new PlaylistRebuildException("The playlist artwork could not be read.");
            var value = sourceArtwork.RequireValue();
            artwork = new(value.Bytes, value.ContentType);
        }
        else
        {
            var existingArtwork = await target.ReadArtworkAsync(context, link.TargetPlaylistId, old.Value.ArtworkReference, cancellationToken);
            if (existingArtwork.IsSuccess) artwork = existingArtwork.Value;
            else if (existingArtwork.Status != BackendPlaylistTargetStatus.NotFound)
                throw new PlaylistRebuildException("The playlist artwork could not be read.");
        }
        string? stagedId = null;
        var switched = false;
        try
        {
            var created = await target.WriteAsync(context, new(BackendPlaylistWriteMode.Reconcile,
                new(link.SyncName ? projection.Name : old.Value.Name,
                    link.SyncDescription ? projection.Description : old.Value.Description, artwork?.Bytes, artwork?.ContentType),
                ids, $"rebuild:{link.Id:N}:{link.Revision}"), cancellationToken);
            stagedId = created.Value?.Snapshot.BackendPlaylistId ?? created.RecoveryPlaylistId;
            if (!created.IsSuccess || created.Value == null)
                throw new PlaylistRebuildException("The replacement Jellyfin playlist could not be created.");
            var snapshotAfter = created.Value.Snapshot;
            if (!snapshotAfter.Members.Select(item => item.BackendItemId).SequenceEqual(ids))
                throw new PlaylistRebuildException("Jellyfin did not keep the requested playlist order.");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id, cancellationToken);
            if (current.Revision != link.Revision) throw new DbUpdateConcurrencyException();
            await PlaylistDestinationChange.ApplyAsync(db, current, stagedId, _clock.UtcNow, cancellationToken);
            var runId = Guid.CreateVersion7();
            db.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
            {
                Id = runId,
                OwnerUserId = owner.Id,
                PlaylistLinkId = link.Id,
                PlaylistSourceSnapshotId = projection.SnapshotId,
                Generation = _clock.UtcNow.UtcTicks,
                IdempotencyKey = $"rebuild:{runId:N}",
                RuleVersion = link.RuleVersion,
                MaterializationMode = PlaylistMaterializationMode.Recreate,
                State = PlaylistSyncState.Succeeded,
                TargetRevisionBefore = old.Value.Fingerprint,
                TargetRevisionAfter = snapshotAfter.Fingerprint,
                PlannedTargetTrackCount = ids.Length,
                VerifiedTargetTrackCount = ids.Length,
                VerifiedTargetDurationMilliseconds = snapshotAfter.DurationMilliseconds,
                VerificationCode = "verified",
                StartedAt = _clock.UtcNow,
                CompletedAt = _clock.UtcNow,
                VerifiedAt = _clock.UtcNow,
                Revision = 1
            });
            var tracks = await db.LibraryTracks.AsNoTracking().Where(item => item.OwnerUserId == owner.Id &&
                item.Protocol == "jellyfin" && item.BackendInstanceId == owner.BackendInstanceId && ids.Contains(item.BackendItemId))
                .ToListAsync(cancellationToken);
            var sourceEntries = await db.PlaylistSourceEntries.AsNoTracking()
                .Where(item => item.PlaylistSourceSnapshotId == projection.SnapshotId).ToListAsync(cancellationToken);
            var countedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in projection.Entries)
            {
                var sourceEntry = sourceEntries.Single(item => item.SourcePosition == entry.Position);
                var track = entry.BackendItemId == null ? null : tracks.FirstOrDefault(item => item.BackendItemId == entry.BackendItemId);
                var included = entry.RouteKind == "local" && track != null && countedIds.Add(track.BackendItemId);
                db.PlaylistSyncEntryResults.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    PlaylistSyncRunId = runId,
                    PlaylistSourceEntryId = sourceEntry.Id,
                    TrackMatchId = sourceEntry.PublishedTrackMatchId,
                    LibraryTrackId = track?.Id,
                    SourcePosition = entry.Position,
                    TargetPosition = included ? Array.IndexOf(ids, track!.BackendItemId) : null,
                    Outcome = included ? PlaylistEntryOutcome.Added : PlaylistEntryOutcome.Skipped,
                    OutcomeCode = included ? PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem :
                        entry.RouteKind == "external" ? PlaylistMaterializationOutcomeCodes.SkippedExternalOnlyForBackend :
                        PlaylistMaterializationOutcomeCodes.SkippedUnresolved
                });
            }
            var memberships = await db.PlaylistTargetMemberships.Where(item => item.PlaylistLinkId == link.Id).ToListAsync(cancellationToken);
            foreach (var track in tracks)
            {
                var membership = memberships.SingleOrDefault(item => item.LibraryTrackId == track.Id);
                if (membership == null)
                {
                    membership = new() { Id = Guid.CreateVersion7(), PlaylistLinkId = link.Id, LibraryTrackId = track.Id, CreatedAt = _clock.UtcNow };
                    db.PlaylistTargetMemberships.Add(membership);
                }
                membership.CreatedBySyncRunId = runId;
                membership.TargetEntryId = track.BackendItemId;
                membership.LastKnownPosition = Array.IndexOf(ids, track.BackendItemId);
                membership.Active = true;
                membership.RemovalConfirmedAt = null;
                membership.UpdatedAt = _clock.UtcNow;
                membership.Revision++;
            }
            current.TargetPlaylistId = stagedId;
            current.DestinationMode = PlaylistDestinationMode.New;
            current.UpdatedAt = _clock.UtcNow;
            current.Revision++;
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = execution.Principal.UserId,
                Category = "playlist-link",
                Action = "rebuild",
                Outcome = "success",
                SubjectType = "playlist-link",
                SubjectId = link.Id.ToString(),
                CorrelationId = execution.CorrelationId,
                CreatedAt = _clock.UtcNow,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { oldPlaylistId = link.TargetPlaylistId, newPlaylistId = stagedId })
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            switched = true;
            var deleted = await target.DeleteAsync(context, link.TargetPlaylistId, cancellationToken);
            if (!deleted.IsSuccess) throw new PlaylistRebuildException("The rebuilt playlist was saved, but the old Jellyfin copy could not be removed.");
            return current;
        }
        finally
        {
            if (!switched && stagedId != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await target.DeleteAsync(context, stagedId, cleanup.Token);
            }
        }
    }
}
