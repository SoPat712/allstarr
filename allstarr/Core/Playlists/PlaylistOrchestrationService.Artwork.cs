using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed partial class PlaylistOrchestrationService
{
    public async Task EnsureSpotifyArtworkRepairsAsync(CancellationToken cancellationToken = default)
    {
        const string prefix = "spotify-artwork-repair:v1:";
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var keys = await db.Jobs.AsNoTracking().Where(item => item.Type == "playlist.materialize" &&
            item.IdempotencyKey.StartsWith(prefix)).Select(item => item.IdempotencyKey).ToListAsync(cancellationToken);
        var queued = keys.Select(key => Guid.TryParse(key[prefix.Length..], out var id) ? id : Guid.Empty).ToArray();
        var links = await db.PlaylistLinks.AsNoTracking().Where(item => item.Enabled && item.SourceProviderId == "spotify" &&
            item.ImportMode == PlaylistImportMode.Linked && item.Mode != PlaylistLinkMode.Virtual &&
            item.TargetProtocol == "jellyfin" && item.TargetPlaylistId != null && !queued.Contains(item.Id))
            .OrderBy(item => item.Id).Take(25).ToListAsync(cancellationToken);
        foreach (var link in links)
            await _jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
                "playlist.materialize", prefix + link.Id, new(link.Id, 1, ArtworkOnly: true), link.OwnerUserId,
                ProviderAccountId: link.ProviderAccountId, Capability: "playlist", CorrelationId: "spotify-artwork-repair"), cancellationToken);
    }

    public async Task<DurableJobCompletion> RepairSpotifyArtworkAsync(ProtocolExecutionContext execution,
        Guid playlistLinkId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == playlistLinkId, cancellationToken);
        var actor = execution.RequireActor();
        PersistenceGuard.RequireOwner(actor, link.OwnerUserId);
        RequireLinkExecutionContext(execution, actor, link);
        if (!link.Enabled || link.SourceProviderId != "spotify" || link.ImportMode != PlaylistImportMode.Linked ||
            link.Mode == PlaylistLinkMode.Virtual || link.TargetProtocol != "jellyfin" || link.TargetPlaylistId == null)
            return DurableJobCompletion.Success();
        await using var accountLock = await _locks.AcquireAsync(
            link.ProviderAccountId.HasValue ? $"playlist-account:{link.ProviderAccountId:N}" : $"playlist-public:{link.OwnerUserId:N}:{link.SourceProviderId}", cancellationToken);
        var summary = await _source.CollectArtworkSummaryAsync(execution, link, cancellationToken);
        if (summary.ProviderId != link.SourceProviderId || summary.ProviderAccountId != link.ProviderAccountId)
            throw new UnauthorizedAccessException("The artwork summary is outside the selected source scope.");
        if (string.IsNullOrWhiteSpace(summary.SourceOwnerId) || summary.SourceOwnerId == "unknown-owner")
            return DurableJobCompletion.Retry("playlist_artwork_owner_unavailable", "The playlist owner is unavailable.", TimeSpan.FromMinutes(2));
        await CheckSpotifyArtworkSyncAsync(execution, link, summary, cancellationToken);
        if (!link.SyncArtwork || !IsSpotifyPersonalizedPlaylist(summary)) return DurableJobCompletion.Success();
        if (summary.ArtworkReferenceKey == null)
            return DurableJobCompletion.Retry("playlist_artwork_unavailable", "The playlist cover is temporarily unavailable.", TimeSpan.FromMinutes(2));
        var artwork = await _source.ResolveArtworkAsync(execution, link, new(new ProviderArtworkReference(
            new("spotify", ProviderResourceKind.Playlist, link.SourcePlaylistId), revision: summary.SourceRevision)), cancellationToken);
        if (!artwork.IsSuccess)
            return DurableJobCompletion.Retry("playlist_artwork_unavailable", "The playlist cover is temporarily unavailable.", TimeSpan.FromMinutes(2));
        var current = await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == link.Id, cancellationToken);
        if (current.Revision != link.Revision || current.TargetPlaylistId != link.TargetPlaylistId)
            return DurableJobCompletion.Retry("playlist_changed", "The playlist settings changed during cover repair.");
        var image = artwork.RequireValue();
        var result = await _targets.Resolve(link.TargetProtocol).WriteArtworkOnlyAsync(
            new(link.TargetBackendInstanceId, execution.VerifiedBackendPrincipalId, link.TargetCredentialReferenceId?.ToString()),
            link.TargetPlaylistId, new(image.Bytes, image.ContentType), cancellationToken);
        return result.IsSuccess ? DurableJobCompletion.Success() :
            DurableJobCompletion.Retry(result.ErrorCode ?? "playlist_artwork_write_failed", "The playlist cover could not be updated.");
    }

    private static bool IsSpotifyPersonalizedPlaylist(CollectedPlaylistSourceSnapshot source)
    {
        var owner = string.Equals(source.SourceOwnerId, "spotify", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source.SourceOwnerId, "spotify:user:spotify", StringComparison.OrdinalIgnoreCase);
        var name = source.Name.Trim();
        return owner && (name.Equals("Release Radar", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Discover Weekly", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("On Repeat", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Repeat Rewind", StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith("Daily Mix", StringComparison.OrdinalIgnoreCase));
    }

    private async Task CheckSpotifyArtworkSyncAsync(ProtocolExecutionContext execution, PlaylistLinkRecord link,
        CollectedPlaylistSourceSnapshot source, CancellationToken cancellationToken)
    {
        if (link.SourceProviderId != "spotify" || link.ImportMode != PlaylistImportMode.Linked ||
            link.Mode == PlaylistLinkMode.Virtual || string.IsNullOrWhiteSpace(source.SourceOwnerId) ||
            source.SourceOwnerId == "unknown-owner") return;
        const string kind = "playlist-artwork-policy";
        var markerHash = Hash($"{link.Id:N}:{link.TargetProtocol}:{link.TargetBackendInstanceId}:{execution.VerifiedBackendPrincipalId}");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var current = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id && item.OwnerUserId == link.OwnerUserId,
            cancellationToken);
        var checkedBefore = await db.ExternalMetadataSnapshots.AnyAsync(item => item.OwnerUserId == link.OwnerUserId &&
            item.ProviderAccountId == link.ProviderAccountId && item.ProviderId == "spotify" && item.ResourceKind == kind &&
            item.ExternalIdHash == markerHash && item.Protocol == link.TargetProtocol &&
            item.BackendInstanceId == link.TargetBackendInstanceId && item.BackendPrincipalId == execution.VerifiedBackendPrincipalId,
            cancellationToken);
        if (!checkedBefore)
        {
            if (!current.SyncArtwork && IsSpotifyPersonalizedPlaylist(source))
            {
                current.SyncArtwork = true;
                current.Revision++;
                current.UpdatedAt = _clock.UtcNow;
            }
            const string payload = "{\"checked\":true}";
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = link.OwnerUserId,
                ProviderAccountId = link.ProviderAccountId,
                BackendInstanceId = link.TargetBackendInstanceId,
                BackendPrincipalId = execution.VerifiedBackendPrincipalId,
                Protocol = link.TargetProtocol,
                ProviderId = "spotify",
                ResourceKind = kind,
                ExternalIdHash = markerHash,
                SnapshotVersion = 1,
                ProviderRevision = "spotify-artwork-v1",
                PayloadJson = payload,
                PayloadSha256 = Hash(payload),
                CorrelationId = execution.CorrelationId,
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        link.SyncArtwork = current.SyncArtwork;
        link.Revision = current.Revision;
        link.UpdatedAt = current.UpdatedAt;
    }
}
