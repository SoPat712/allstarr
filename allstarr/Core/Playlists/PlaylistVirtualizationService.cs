using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Playlists.Targets;
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
    TrackMatchState MatchState,
    string? SourceProviderId = null,
    string? SourceExternalId = null,
    TrackRouteKind RouteKind = TrackRouteKind.Local,
    string? RouteProviderId = null,
    string? RouteExternalId = null,
    PlaylistSourceIdentity? SourceIdentity = null,
    PlaylistSourceMetadata? SourceMetadata = null,
    string? NativePlaylistEntryId = null,
    string? NativeEntryJson = null);

public sealed record VirtualPlaylistReadModel(
    string ProtocolId,
    Guid LinkId,
    Guid SnapshotId,
    string Name,
    string? Description,
    string? ArtworkReferenceKey,
    string SourceProviderId,
    string SourcePlaylistId,
    string SourceRevision,
    PlaylistLinkMode Mode,
    IReadOnlyList<VirtualPlaylistTrack> Tracks,
    PlaylistProjectionMode ProjectionMode = PlaylistProjectionMode.Resolved);

public sealed record VirtualPlaylistArtworkSource(string ProviderId, string PlaylistId);

public interface IPlaylistVirtualizationService
{
    Task<IReadOnlyList<VirtualPlaylistReadModel>> ListAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken = default);

    Task<VirtualPlaylistReadModel?> ReadAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default);

    Task<VirtualPlaylistReadModel?> ReadBySourceAsync(
        ProtocolExecutionContext context,
        string sourceProviderId,
        string sourcePlaylistId,
        CancellationToken cancellationToken = default);

    Task<VirtualPlaylistArtworkSource?> ResolvePublicArtworkSourceAsync(
        string protocolId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects every row of an immutable provider playlist snapshot for protocol reads.
/// This is deliberately read-only: virtual and hybrid reads never create or mutate a backend playlist.
/// </summary>
public sealed class PlaylistVirtualizationService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurablePlaylistProjectionReader projections,
    IBackendPlaylistTargetResolver? targets = null) : IPlaylistVirtualizationService
{
    public const string IdPrefix = "allstarr-vpl-";
    public const string UnresolvedItemPrefix = "allstarr-unresolved-";

    public static string CreateProtocolId(Guid linkId) => $"{IdPrefix}{linkId:N}";

    public static bool IsUnresolvedItemId(string? value) =>
        value?.StartsWith(UnresolvedItemPrefix, StringComparison.OrdinalIgnoreCase) == true;

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
        BackendPlaylistSnapshot? targetSnapshot = null;
        if (link.ProjectionMode == PlaylistProjectionMode.Target)
        {
            if (targets == null || string.IsNullOrWhiteSpace(link.TargetPlaylistId)) return null;
            var targetResult = await targets.Resolve(link.TargetProtocol).ReadAsync(
                new BackendPlaylistTargetContext(
                    link.TargetBackendInstanceId,
                    context.VerifiedBackendPrincipalId,
                    link.TargetCredentialReferenceId?.ToString(),
                    link.TenantId),
                link.TargetPlaylistId,
                cancellationToken);
            if (!targetResult.IsSuccess || targetResult.Value == null) return null;
            targetSnapshot = targetResult.Value;
        }
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
        var resolvedTracks = projection.Entries
            .Select(item => ToResolvedVirtualTrack(item, libraryTracks, link.SourceProviderId))
            .ToArray();
        var resolvedByPosition = resolvedTracks.ToDictionary(item => item.SourcePosition);
        var sourceTracks = projection.SourceEntries
            .Select(item => ToSourceVirtualTrack(
                item,
                resolvedByPosition.GetValueOrDefault(item.Position)))
            .ToArray();
        var targetTracks = targetSnapshot == null
            ? null
            : ToTargetVirtualTracks(targetSnapshot);
        var tracks = PlaylistProjectionSelector.Select(
            link.ProjectionMode,
            sourceTracks,
            resolvedTracks,
            targetTracks);
        if (tracks == null) return null;
        var name = targetSnapshot == null ? projection.Name : targetSnapshot.Name;
        var description = targetSnapshot == null ? projection.Description : targetSnapshot.Description;
        var artwork = targetSnapshot == null
            ? projection.ArtworkReferenceKey
            : targetSnapshot.ArtworkReference;
        return new VirtualPlaylistReadModel(protocolId, link.Id, projection.SnapshotId,
            name, description, artwork, link.SourceProviderId,
            link.SourcePlaylistId, snapshot.ProviderRevision, link.Mode, tracks, link.ProjectionMode);
    }

    public async Task<VirtualPlaylistReadModel?> ReadBySourceAsync(
        ProtocolExecutionContext context,
        string sourceProviderId,
        string sourcePlaylistId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Actor == null ||
            string.IsNullOrWhiteSpace(sourceProviderId) ||
            string.IsNullOrWhiteSpace(sourcePlaylistId))
            return null;

        var actor = context.Actor;
        var providerId = sourceProviderId.Trim().ToLowerInvariant();
        var playlistId = sourcePlaylistId.Trim();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var linkId = await db.PlaylistLinks.AsNoTracking()
            .Where(item =>
                item.TenantId == actor.TenantId &&
                item.OwnerUserId == actor.EffectiveUserId &&
                item.TargetBackendInstanceId == context.BackendInstanceId &&
                item.SourceProviderId == providerId &&
                item.SourcePlaylistId == playlistId &&
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
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return linkId == null
            ? null
            : await ReadAsync(context, CreateProtocolId(linkId.Value), cancellationToken);
    }

    public async Task<VirtualPlaylistArtworkSource?> ResolvePublicArtworkSourceAsync(
        string protocolId,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseProtocolId(protocolId, out var linkId)) return null;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PlaylistLinks.AsNoTracking()
            .Where(item =>
                item.Id == linkId &&
                item.TargetProtocol == "jellyfin" &&
                item.Enabled &&
                (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid))
            .Select(item => new VirtualPlaylistArtworkSource(
                item.SourceProviderId,
                item.SourcePlaylistId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static VirtualPlaylistTrack ToResolvedVirtualTrack(
        DurablePlaylistEntryProjection entry,
        IReadOnlyDictionary<string, LibraryTrackRecord> libraryTracks,
        string sourceProviderId)
    {
        if (entry.BackendItemId != null &&
            libraryTracks.TryGetValue(entry.BackendItemId, out var local))
            return new(entry.Position, local.BackendItemId, local.Title, local.Artist,
                local.Album, local.AlbumArtist, local.DurationMilliseconds,
                local.CoverArtReference, entry.MatchState ?? TrackMatchState.Unresolved,
                sourceProviderId, null, TrackRouteKind.Local);
        var artist = entry.Artists.FirstOrDefault();
        if (entry.RouteKind == "external" && entry.RouteProviderId != null)
        {
            return new(entry.Position, $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}",
                entry.Title, string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
                entry.Album, null,
                entry.DurationMilliseconds, null,
                entry.MatchState ?? TrackMatchState.Unresolved,
                entry.RouteProviderId, entry.ExternalId, TrackRouteKind.External,
                entry.RouteProviderId, entry.ExternalId);
        }

        return new(entry.Position, $"{UnresolvedItemPrefix}{entry.ExternalId}",
            entry.Title, string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
            entry.Album, null,
            entry.DurationMilliseconds, null, entry.MatchState ?? TrackMatchState.Unresolved,
            sourceProviderId, null, TrackRouteKind.Unresolved);
    }

    internal static IReadOnlyList<VirtualPlaylistTrack> ToTargetVirtualTracks(
        BackendPlaylistSnapshot snapshot) => snapshot.Members
        .Select((member, position) => new VirtualPlaylistTrack(
            position,
            member.BackendItemId,
            member.BackendItemId,
            "Unknown Artist",
            null,
            null,
            member.DurationMilliseconds,
            null,
            TrackMatchState.Unresolved,
            RouteKind: TrackRouteKind.Local,
            NativePlaylistEntryId: member.EntryId,
            NativeEntryJson: member.NativeEntryJson))
        .ToArray();

    internal static VirtualPlaylistTrack ToSourceVirtualTrack(
        DurablePlaylistSourceEntryProjection source,
        VirtualPlaylistTrack? resolved)
    {
        var identity = source.Identity;
        var metadata = source.Metadata;
        var artist = metadata.Artists?.FirstOrDefault();
        var sourceArtist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
        var routeKind = resolved?.RouteKind == TrackRouteKind.Local ||
                        resolved?.RouteKind == TrackRouteKind.External &&
                        !string.IsNullOrWhiteSpace(resolved.RouteProviderId) &&
                        !string.IsNullOrWhiteSpace(resolved.RouteExternalId)
            ? resolved.RouteKind
            : TrackRouteKind.Unresolved;
        return new(
            source.Position,
            routeKind == TrackRouteKind.Unresolved
                ? $"{UnresolvedItemPrefix}{identity.ExternalIdHash}"
                : resolved!.BackendItemId,
            metadata.Title ?? "Unknown",
            sourceArtist,
            metadata.Album,
            null,
            metadata.DurationMilliseconds,
            identity.ExternalId == null
                ? null
                : $"ext-{identity.ProviderId}-song-{identity.ExternalId}",
            resolved?.MatchState ?? TrackMatchState.Unresolved,
            identity.ProviderId,
            identity.ExternalId,
            routeKind,
            routeKind == TrackRouteKind.External ? resolved!.RouteProviderId : null,
            routeKind == TrackRouteKind.External ? resolved!.RouteExternalId : null,
            identity,
            metadata);
    }
}
