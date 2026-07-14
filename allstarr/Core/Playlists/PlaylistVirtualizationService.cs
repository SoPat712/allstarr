using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record VirtualPlaylistTrack(
    int SourcePosition,
    string BackendItemId,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    long DurationMilliseconds,
    string? CoverArtReference,
    TrackMatchState MatchState);

public sealed record VirtualPlaylistReadModel(
    string ProtocolId,
    Guid LinkId,
    Guid SnapshotId,
    string Name,
    string? Description,
    string? ArtworkReferenceKey,
    string SourceProviderId,
    string SourceRevision,
    PlaylistLinkMode Mode,
    IReadOnlyList<VirtualPlaylistTrack> Tracks);

public interface IPlaylistVirtualizationService
{
    Task<VirtualPlaylistReadModel?> ReadAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects an immutable provider playlist snapshot onto accepted local-library matches.
/// This is deliberately read-only: virtual and hybrid reads never create or mutate a backend playlist.
/// </summary>
public sealed class PlaylistVirtualizationService(
    IDbContextFactory<AllstarrDbContext> contextFactory) : IPlaylistVirtualizationService
{
    public const string IdPrefix = "allstarr-vpl-";

    public static string CreateProtocolId(Guid linkId) => $"{IdPrefix}{linkId:N}";

    public static bool TryParseProtocolId(string? value, out Guid linkId)
    {
        linkId = default;
        return value?.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase) == true &&
               Guid.TryParseExact(value[IdPrefix.Length..], "N", out linkId);
    }

    public async Task<VirtualPlaylistReadModel?> ReadAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryParseProtocolId(protocolId, out var linkId) || context.Actor == null)
            return null;

        var actor = context.Actor;
        var protocol = context.Protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic";
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == linkId && item.TenantId == actor.TenantId &&
            item.OwnerUserId == actor.EffectiveUserId &&
            item.TargetBackendInstanceId == context.BackendInstanceId &&
            (context.Protocol == ProtocolKind.Jellyfin
                ? item.TargetProtocol == "jellyfin"
                : item.TargetProtocol == "subsonic" || item.TargetProtocol == "opensubsonic" || item.TargetProtocol == "navidrome") &&
            (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid),
            cancellationToken);
        if (link == null || context.LibraryScopeId is { Length: > 0 } requestedLibrary &&
            !requestedLibrary.Equals(link.LibraryScopeId, StringComparison.Ordinal))
            return null;

        var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == link.Id)
            .OrderByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;

        var entries = await db.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderBy(item => item.SourcePosition)
            .ToListAsync(cancellationToken);
        var externalIds = entries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray();
        var overrides = await db.ManualTrackOverrides.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == link.OwnerUserId &&
                           item.LibraryScopeId == link.LibraryScopeId && item.RevokedAt == null &&
                           externalIds.Contains(item.ExternalSnapshotId))
            .ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
        var decisions = (await db.TrackMatches.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId && item.OwnerUserId == link.OwnerUserId &&
                               item.LibraryScopeId == link.LibraryScopeId &&
                               externalIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.DecisionVersion)
                .ToListAsync(cancellationToken))
            .GroupBy(item => item.ExternalSnapshotId)
            .ToDictionary(group => group.Key, group => group.First());

        var selected = entries.Select(entry =>
        {
            overrides.TryGetValue(entry.ExternalMetadataSnapshotId, out var manual);
            decisions.TryGetValue(entry.ExternalMetadataSnapshotId, out var decision);
            var state = manual?.Decision == ManualOverrideDecision.Pin
                ? TrackMatchState.Pinned
                : manual?.Decision == ManualOverrideDecision.Reject
                    ? TrackMatchState.Rejected
                    : decision?.State ?? TrackMatchState.Unresolved;
            var trackId = manual?.Decision == ManualOverrideDecision.Pin
                ? manual.LibraryTrackId
                : state == TrackMatchState.Accepted && decision!.Confidence >= decision.Threshold
                    ? decision.LibraryTrackId
                    : null;
            return (Entry: entry, State: state, TrackId: trackId);
        }).Where(item => item.TrackId.HasValue).ToList();
        var libraryIds = selected.Select(item => item.TrackId!.Value).Distinct().ToArray();
        var libraryTracks = await db.LibraryTracks.AsNoTracking().Where(item =>
                libraryIds.Contains(item.Id) && item.TenantId == actor.TenantId &&
                item.OwnerUserId == link.OwnerUserId && item.LibraryScopeId == link.LibraryScopeId &&
                item.BackendInstanceId == link.TargetBackendInstanceId &&
                (context.Protocol == ProtocolKind.Jellyfin
                    ? item.Protocol == "jellyfin"
                    : item.Protocol == "subsonic" || item.Protocol == "opensubsonic" || item.Protocol == "navidrome"))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var tracks = selected
            .Where(item => libraryTracks.ContainsKey(item.TrackId!.Value))
            .Select(item =>
            {
                var track = libraryTracks[item.TrackId!.Value];
                return new VirtualPlaylistTrack(item.Entry.SourcePosition, track.BackendItemId,
                    track.Title, track.Artist, track.Album, track.AlbumArtist,
                    track.DurationMilliseconds, track.CoverArtReference, item.State);
            }).ToList();
        return new VirtualPlaylistReadModel(protocolId, link.Id, snapshot.Id, snapshot.Name,
            snapshot.Description, snapshot.ArtworkReferenceKey, link.SourceProviderId,
            snapshot.ProviderRevision, link.Mode, tracks);
    }
}
