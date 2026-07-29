using allstarr.Core.Matching;
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
    long? DurationMilliseconds,
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
    Task<IReadOnlyList<VirtualPlaylistReadModel>> ListAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken = default);

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
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurablePlaylistProjectionReader projections) : IPlaylistVirtualizationService
{
    public const string IdPrefix = "allstarr-vpl-";

    public static string CreateProtocolId(Guid linkId) => $"{IdPrefix}{linkId:N}";

    public static bool TryParseProtocolId(string? value, out Guid linkId)
    {
        linkId = default;
        return value?.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase) == true &&
               Guid.TryParseExact(value[IdPrefix.Length..], "N", out linkId);
    }

    public async Task<IReadOnlyList<VirtualPlaylistReadModel>> ListAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Actor == null) return [];

        var actor = context.Actor;
        Guid[] linkIds;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            linkIds = await db.PlaylistLinks.AsNoTracking()
                .Where(item =>
                    item.TenantId == actor.TenantId &&
                    item.OwnerUserId == actor.EffectiveUserId &&
                    item.TargetBackendInstanceId == context.BackendInstanceId &&
                    (context.Protocol == ProtocolKind.Jellyfin
                        ? item.TargetProtocol == "jellyfin"
                        : item.TargetProtocol == "subsonic" ||
                          item.TargetProtocol == "opensubsonic" ||
                          item.TargetProtocol == "navidrome") &&
                    item.Enabled &&
                    (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid) &&
                    (string.IsNullOrEmpty(context.LibraryScopeId) ||
                     item.LibraryScopeId == context.LibraryScopeId))
                .OrderBy(item => item.CreatedAt)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken);
        }

        var result = new List<VirtualPlaylistReadModel>(linkIds.Length);
        // ponytail: one scoped read per visible link; batch only if measured browse latency requires it.
        foreach (var linkId in linkIds)
        {
            var playlist = await ReadAsync(context, CreateProtocolId(linkId), cancellationToken);
            if (playlist != null) result.Add(playlist);
        }
        return result;
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
            item.Enabled && (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid),
            cancellationToken);
        if (link == null || context.LibraryScopeId is { Length: > 0 } requestedLibrary &&
            !requestedLibrary.Equals(link.LibraryScopeId, StringComparison.Ordinal))
            return null;

        var projection = await projections.ReadByLinkIdAsync(
            actor.TenantId, link.OwnerUserId, link.Id, cancellationToken);
        if (projection == null) return null;
        var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking()
            .SingleAsync(item => item.Id == projection.SnapshotId, cancellationToken);
        var backendIds = projection.Entries
            .Where(item => item.RouteKind == "local" && item.BackendItemId != null)
            .Select(item => item.BackendItemId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var libraryTracks = await db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.OwnerUserId == link.OwnerUserId &&
                           item.LibraryScopeId == link.LibraryScopeId &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           backendIds.Contains(item.BackendItemId))
            .ToDictionaryAsync(item => item.BackendItemId, StringComparer.Ordinal, cancellationToken);
        var tracks = projection.Entries
            .Select(item => ToVirtualTrack(item, libraryTracks))
            .Where(item => item != null)
            .Select(item => item!)
            .ToList();
        return new VirtualPlaylistReadModel(protocolId, link.Id, projection.SnapshotId, projection.Name,
            projection.Description, projection.ArtworkReferenceKey, link.SourceProviderId,
            snapshot.ProviderRevision, link.Mode, tracks);
    }

    private static VirtualPlaylistTrack? ToVirtualTrack(
        DurablePlaylistEntryProjection entry,
        IReadOnlyDictionary<string, LibraryTrackRecord> libraryTracks)
    {
        if (entry.BackendItemId != null &&
            libraryTracks.TryGetValue(entry.BackendItemId, out var local))
            return new(entry.Position, local.BackendItemId, local.Title, local.Artist,
                local.Album, local.AlbumArtist, local.DurationMilliseconds,
                local.CoverArtReference, entry.MatchState ?? TrackMatchState.Unresolved);
        var artist = entry.Artists.FirstOrDefault();
        return entry.RouteKind == "external" && entry.RouteProviderId != null &&
               !string.IsNullOrWhiteSpace(artist)
            ? new(entry.Position, $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}",
                entry.Title, artist, entry.Album, null, entry.DurationMilliseconds, null,
                entry.MatchState ?? TrackMatchState.Unresolved)
            : null;
    }
}
