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
public sealed record PlaylistAppRestoreResult(PlaylistLinkRecord Link, Guid? JobId);

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
        link.DestinationMode == PlaylistDestinationMode.New;

    public static async Task<ExternalMetadataSnapshotRecord?> FindAsync(AllstarrDbContext db, PlaylistLinkRecord link,
        CancellationToken cancellationToken) => await Rows(db).SingleOrDefaultAsync(item =>
            item.OwnerUserId == link.OwnerUserId && item.ExternalIdHash == Key(link.Id), cancellationToken);

    public static async Task<int> NextVersionAsync(AllstarrDbContext db, PlaylistLinkRecord link, CancellationToken cancellationToken) =>
        (await History(db).Where(item => item.OwnerUserId == link.OwnerUserId && item.ExternalIdHash == Key(link.Id))
            .MaxAsync(item => (int?)item.SnapshotVersion, cancellationToken) ?? 0) + 1;

    public static ExternalMetadataSnapshotRecord Marker(PlaylistLinkRecord link, ProtocolExecutionContext execution,
        DateTimeOffset now, int version, bool removed)
    {
        var payload = JsonSerializer.Serialize(new { removed });
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
    public async Task<PlaylistLinkRecord> RemoveFromAppsAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        await using var writeLock = await _locks.AcquireAsync($"playlist-apps:{playlistLinkId:N}", cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var link = await LoadOwnAppLinkAsync(db, execution, playlistLinkId, cancellationToken);
        if (link.Revision != expectedRevision) throw new DbUpdateConcurrencyException();
        if (!PlaylistAppRemoval.CanRemove(link))
            throw new InvalidOperationException("This playlist was not created by Allstarr, so it cannot be removed from your apps here.");
        if (await PlaylistAppRemoval.FindAsync(db, link, cancellationToken) != null) return link;
        if (link.TargetPlaylistId != null && await db.PlaylistLinks.AnyAsync(item => item.Id != link.Id &&
                item.TargetProtocol == link.TargetProtocol && item.TargetBackendInstanceId == link.TargetBackendInstanceId &&
                item.TargetPlaylistId == link.TargetPlaylistId, cancellationToken))
            throw new InvalidOperationException("Another listener uses this app playlist, so it cannot be removed here.");
        var targetId = link.TargetPlaylistId;
        link.Enabled = false;
        await SetAppScheduleAsync(db, link, false, cancellationToken);
        await PlaylistDestinationChange.ApplyAsync(db, link, null, _clock.UtcNow, cancellationToken);
        link.TargetPlaylistId = null;
        link.UpdatedAt = _clock.UtcNow;
        link.Revision++;
        db.ExternalMetadataSnapshots.Add(PlaylistAppRemoval.Marker(link, execution, _clock.UtcNow,
            await PlaylistAppRemoval.NextVersionAsync(db, link, cancellationToken), true));
        await RecordAppActivityAsync(db, execution, link, false, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (targetId != null)
        {
            var target = _targets.Resolve(link.TargetProtocol);
            var context = new BackendPlaylistTargetContext(link.TargetBackendInstanceId,
                execution.VerifiedBackendPrincipalId, link.TargetCredentialReferenceId?.ToString());
            var deleted = await target.DeleteAsync(context, targetId, cancellationToken);
            if (!deleted.IsSuccess && deleted.Status != BackendPlaylistTargetStatus.NotFound)
                throw new PlaylistAppRemovalException("The Jellyfin playlist could not be removed. Try again.");
        }
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
        link.Enabled = true;
        link.UpdatedAt = _clock.UtcNow;
        link.Revision++;
        await SetAppScheduleAsync(db, link, true, cancellationToken);
        db.ExternalMetadataSnapshots.Add(PlaylistAppRemoval.Marker(link, execution, _clock.UtcNow,
            marker.SnapshotVersion + 1, false));
        await RecordAppActivityAsync(db, execution, link, true, cancellationToken);
        Guid? jobId = null;
        if (enqueueMaterialization)
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
        return new(link, jobId);
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
