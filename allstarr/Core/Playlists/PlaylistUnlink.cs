using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record PlaylistUnlinkResult(PlaylistLinkRecord Link, string PlaylistName, int KeptSongs);
internal sealed record PlaylistUnlinkState(string? TargetPlaylistId, PlaylistDestinationMode DestinationMode, bool SyncName,
    bool SyncDescription, bool SyncArtwork, Guid? ScheduleId, string PlaylistName, int KeptSongs);

// An unlinked playlist stays in the media server as an ordinary playlist. The link row, its snapshots and matches stay
// for history and for linking the same source again; the newest marker says whether the link is ended.
internal static class PlaylistUnlink
{
    private const string ResourceKind = "playlist-unlink";
    private static IQueryable<ExternalMetadataSnapshotRecord> History(AllstarrDbContext db) =>
        db.ExternalMetadataSnapshots.Where(item => item.ProviderId == "allstarr" && item.ResourceKind == ResourceKind);
    public static IQueryable<ExternalMetadataSnapshotRecord> Rows(AllstarrDbContext db)
    {
        var history = History(db);
        return history.Where(item => item.ProviderRevision == "unlinked" && !history.Any(newer =>
            newer.OwnerUserId == item.OwnerUserId && newer.ExternalIdHash == item.ExternalIdHash &&
            newer.SnapshotVersion > item.SnapshotVersion));
    }

    public static Task<ExternalMetadataSnapshotRecord?> FindAsync(AllstarrDbContext db, PlaylistLinkRecord link,
        CancellationToken cancellationToken) => Rows(db).SingleOrDefaultAsync(item =>
            item.OwnerUserId == link.OwnerUserId && item.ExternalIdHash == PlaylistAppRemoval.Key(link.Id), cancellationToken);

    // Each time the link is made again its first import needs a fresh job, so the job key carries this number.
    public static async Task<int> RelinkCountAsync(AllstarrDbContext db, PlaylistLinkRecord link, CancellationToken cancellationToken) =>
        await History(db).CountAsync(item => item.OwnerUserId == link.OwnerUserId &&
            item.ExternalIdHash == PlaylistAppRemoval.Key(link.Id) && item.ProviderRevision == "linked", cancellationToken);

    public static PlaylistUnlinkState Read(ExternalMetadataSnapshotRecord marker) =>
        JsonSerializer.Deserialize<PlaylistUnlinkState>(marker.PayloadJson)!;

    public static async Task<ExternalMetadataSnapshotRecord> MarkerAsync(AllstarrDbContext db, PlaylistLinkRecord link,
        ProtocolExecutionContext execution, DateTimeOffset now, PlaylistUnlinkState? state, CancellationToken cancellationToken)
    {
        var key = PlaylistAppRemoval.Key(link.Id);
        var version = (await History(db).Where(item => item.OwnerUserId == link.OwnerUserId && item.ExternalIdHash == key)
            .MaxAsync(item => (int?)item.SnapshotVersion, cancellationToken) ?? 0) + 1;
        var payload = state == null ? JsonSerializer.Serialize(new { linked = true }) : JsonSerializer.Serialize(state);
        return new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = link.OwnerUserId,
            Protocol = link.TargetProtocol,
            BackendInstanceId = link.TargetBackendInstanceId,
            BackendPrincipalId = execution.VerifiedBackendPrincipalId,
            ProviderId = "allstarr",
            ResourceKind = ResourceKind,
            ExternalIdHash = key,
            SnapshotVersion = version,
            ProviderRevision = state == null ? "linked" : "unlinked",
            PayloadJson = payload,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            CorrelationId = execution.CorrelationId,
            RetrievedAt = now
        };
    }

    // Linking the source again goes back into the playlist it fed before unless another playlist is chosen. The
    // memberships were left as they were, so the songs this link placed are recognised and not added a second time.
    public static async Task<PlaylistLinkRecord> RelinkAsync(AllstarrDbContext db, ProtocolExecutionContext execution,
        Guid linkId, ExternalMetadataSnapshotRecord marker, PlaylistLinkInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == linkId, cancellationToken);
        if (input.ImportMode != link.ImportMode)
            throw new ArgumentException("This source playlist was imported with different settings before. Import it the same way to link it again.", nameof(input));
        var state = Read(marker);
        link.TargetPlaylistId = state.TargetPlaylistId;
        if (input.TargetPlaylistId == null || string.Equals(input.TargetPlaylistId, state.TargetPlaylistId, StringComparison.Ordinal))
        {
            link.DestinationMode = state.DestinationMode;
            link.SyncName = state.SyncName;
            link.SyncDescription = state.SyncDescription;
            link.SyncArtwork = state.SyncArtwork;
        }
        else
        {
            await PlaylistDestinationChange.ApplyAsync(db, link, input.TargetPlaylistId, now, cancellationToken);
            link.TargetPlaylistId = input.TargetPlaylistId;
            link.SyncName = link.SyncDescription = link.SyncArtwork = false;
        }
        link.ScheduleId = input.ScheduleId;
        link.TrackRetention = input.TrackRetention;
        link.Enabled = true;
        link.UpdatedAt = now;
        link.Revision++;
        db.ExternalMetadataSnapshots.Add(await MarkerAsync(db, link, execution, now, null, cancellationToken));
        db.InvalidateFeedReads(link.OwnerUserId);
        await db.SaveChangesAsync(cancellationToken);
        return link;
    }
}

public sealed partial class PlaylistOrchestrationService
{
    public async Task<PlaylistUnlinkResult> UnlinkAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
        CancellationToken cancellationToken = default)
    {
        // The same lock a sync holds while it writes, so a write already under way finishes first and any later
        // one sees the changed revision and stops without writing.
        await using var writeLock = await _locks.AcquireAsync($"playlist-apps:{playlistLinkId:N}", cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await LoadOwnAppLinkAsync(db, execution, playlistLinkId, cancellationToken);
        if (await PlaylistUnlink.FindAsync(db, link, cancellationToken) is { } done)
        {
            var previous = PlaylistUnlink.Read(done);
            return new(link, previous.PlaylistName, previous.KeptSongs);
        }
        if (await PlaylistAppRemoval.FindAsync(db, link, cancellationToken) != null)
            throw new InvalidOperationException("This playlist is not in your apps, so there is nothing to keep. Add it back first or remove its link.");
        if (link.ImportMode != PlaylistImportMode.Linked || PersonalMixService.IsExternalLink(link) || link.TargetPlaylistId == null)
            throw new InvalidOperationException("Only a playlist kept in sync with your media server can be unlinked.");
        var name = await db.PlaylistSourceSnapshots.Where(item => item.PlaylistLinkId == link.Id)
            .OrderByDescending(item => item.SnapshotVersion).Select(item => item.Name).FirstOrDefaultAsync(cancellationToken) ?? "Playlist";
        // Reading the playlist only counts what stays; nothing in the media server is changed.
        var read = await _targets.Resolve(link.TargetProtocol).ReadAsync(AppTargetContext(execution, link), link.TargetPlaylistId, cancellationToken);
        var kept = read.IsSuccess && read.Value != null ? read.Value.Members.Count
            : await db.PlaylistTargetMemberships.CountAsync(item => item.PlaylistLinkId == link.Id && item.Active, cancellationToken);
        if (read.IsSuccess && read.Value != null && !string.IsNullOrWhiteSpace(read.Value.Name)) name = read.Value.Name;
        var state = new PlaylistUnlinkState(link.TargetPlaylistId, link.DestinationMode, link.SyncName, link.SyncDescription,
            link.SyncArtwork, link.ScheduleId, name, kept);
        if (link.ScheduleId is { } scheduleId && !await db.PlaylistLinks.AnyAsync(item => item.Id != link.Id &&
                item.OwnerUserId == link.OwnerUserId && item.ScheduleId == scheduleId, cancellationToken) &&
            await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId && item.OwnerUserId == link.OwnerUserId,
                cancellationToken) is { } schedule)
        {
            schedule.Enabled = false;
            schedule.NextRunAt = null;
            schedule.UpdatedAt = _clock.UtcNow;
            schedule.Revision++;
        }
        link.Enabled = false;
        link.ScheduleId = null;
        link.TargetPlaylistId = null;
        link.UpdatedAt = _clock.UtcNow;
        link.Revision++;
        db.ExternalMetadataSnapshots.Add(await PlaylistUnlink.MarkerAsync(db, link, execution, _clock.UtcNow, state, cancellationToken));
        db.AuditEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = link.OwnerUserId,
            Category = "playlist-link",
            Action = "unlinked",
            Outcome = "success",
            SubjectType = "playlist-link",
            SubjectId = link.Id.ToString(),
            CorrelationId = execution.CorrelationId,
            CreatedAt = _clock.UtcNow,
            DetailsJson = JsonSerializer.Serialize(new
            {
                playlistLinkId = link.Id,
                playlistName = name,
                message = $"{name} stopped syncing and stays in your apps as an ordinary playlist."
            })
        });
        db.InvalidateFeedReads(link.OwnerUserId);
        await db.SaveChangesAsync(cancellationToken);
        return new(link, name, kept);
    }
}
