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
    string? NativeEntryJson = null,
    Guid? SourceEntryId = null)
{
    public string? OriginalItemId { get; init; }
    public int? PlaybackExplicitContentLyrics { get; init; }
}

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
    PlaylistProjectionMode ProjectionMode = PlaylistProjectionMode.Resolved,
    string? TargetPlaylistId = null)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public DateTimeOffset? UpdatedAt { get; init; }
    public int? SummaryTrackCount { get; init; }
    public long? SummaryDurationMilliseconds { get; init; }
    public int TrackCount => SummaryTrackCount ?? Tracks.Count;
    public long DurationMilliseconds => SummaryDurationMilliseconds ?? Tracks.Sum(track => track.DurationMilliseconds ?? 0);
    public string? BackendOwner { get; init; }
    public bool? BackendPublic { get; init; }
    public allstarr.Core.Music.MixCoverInput? MixCover { get; init; }
}

public interface IPlaylistVirtualizationService
{
    Task<IReadOnlyList<VirtualPlaylistReadModel>> ListAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken = default);

    Task<VirtualPlaylistReadModel?> ReadAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default);

    Task<VirtualPlaylistReadModel?> ReadAsync(
        ProtocolExecutionContext context,
        string protocolId,
        PlaylistProjectionMode projectionMode,
        CancellationToken cancellationToken = default);

    Task<VirtualPlaylistReadModel?> ReadBySourceAsync(
        ProtocolExecutionContext context,
        string sourceProviderId,
        string sourcePlaylistId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads immutable provider playlist summaries and projects tracks for individual protocol reads.
/// This is deliberately read-only: virtual and hybrid reads never create or mutate a backend playlist.
/// </summary>
public sealed class PlaylistVirtualizationService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurablePlaylistProjectionReader projections,
    IBackendLibraryAccessResolver libraryAccess,
    IBackendPlaylistTargetResolver? targets = null,
    allstarr.Core.Music.PersonalMixService? personalMixes = null) : IPlaylistVirtualizationService
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

        if (context.Protocol == ProtocolKind.Jellyfin)
            return await ListSummariesAsync(context, cancellationToken);
        var actor = context.Actor;
        Guid[] linkIds;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            linkIds = await db.PlaylistLinks.AsNoTracking()
                .Where(item =>
                    (item.OwnerUserId == actor.EffectiveUserId || item.TargetPlaylistId != null) &&
                    item.TargetBackendInstanceId == context.BackendInstanceId &&
                    (context.Protocol == ProtocolKind.Jellyfin
                        ? item.TargetProtocol == "jellyfin"
                        : item.TargetProtocol == "subsonic" ||
                          item.TargetProtocol == "opensubsonic" ||
                          item.TargetProtocol == "navidrome") &&
                    item.Enabled &&
                    (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid ||
                     context.Protocol == ProtocolKind.Jellyfin))
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
        if (personalMixes != null) result.AddRange(await personalMixes.PublishedAsync(context, cancellationToken));
        return result;
    }

    internal async Task<IReadOnlyList<VirtualPlaylistReadModel>> ListSummariesAsync(
        ProtocolExecutionContext context, CancellationToken token, IReadOnlySet<string>? visibleTargetIds = null)
    {
        if (context.Actor?.EffectiveUserId is not { } owner) return [];
        await using var db = await contextFactory.CreateDbContextAsync(token);
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == owner && user.Enabled &&
            user.BackendType == "jellyfin" && user.BackendInstanceId == context.BackendInstanceId &&
            user.BackendPrincipalId == context.VerifiedBackendPrincipalId, token)) return [];
        var links = await db.PlaylistLinks.AsNoTracking().Where(link => (link.OwnerUserId == owner || link.TargetPlaylistId != null) &&
            link.TargetBackendInstanceId == context.BackendInstanceId && link.TargetProtocol == "jellyfin" && link.Enabled &&
            (link.Mode == PlaylistLinkMode.Virtual || link.Mode == PlaylistLinkMode.Hybrid ||
             link.Mode == PlaylistLinkMode.Materialized && link.TargetPlaylistId != null))
            .OrderBy(link => link.CreatedAt).ToArrayAsync(token);
        var linkIds = links.Select(link => link.Id).ToArray();
        var snapshots = await db.PlaylistSourceSnapshots.AsNoTracking().Where(snapshot =>
            linkIds.Contains(snapshot.PlaylistLinkId) && snapshot.PublishedAt.HasValue)
            .GroupBy(snapshot => snapshot.PlaylistLinkId).Select(group => group.OrderByDescending(snapshot => snapshot.SnapshotVersion)
                .ThenByDescending(snapshot => snapshot.RetrievedAt).First()).ToArrayAsync(token);
        var externalProjections = await projections.ReadByLinkIdsAsync(owner,
            links.Where(allstarr.Core.Music.PersonalMixService.IsExternalLink).Select(link => link.Id).ToArray(), token, owner);
        var result = new List<VirtualPlaylistReadModel>();
        foreach (var link in links)
        {
            if (link.TargetPlaylistId != null)
            {
                if (visibleTargetIds != null)
                {
                    if (!visibleTargetIds.Contains(link.TargetPlaylistId)) continue;
                }
                else
                {
                    if (targets == null) continue;
                    var read = await targets.Resolve(link.TargetProtocol).ReadAsync(new(link.TargetBackendInstanceId,
                        context.VerifiedBackendPrincipalId, link.OwnerUserId == owner ? link.TargetCredentialReferenceId?.ToString() : null),
                        link.TargetPlaylistId, token);
                    if (!read.IsSuccess || read.Value == null) continue;
                }
            }
            var snapshot = snapshots.SingleOrDefault(item => item.PlaylistLinkId == link.Id);
            if (snapshot == null) continue;
            var external = allstarr.Core.Music.PersonalMixService.IsExternalLink(link);
            if (external)
            {
                if (!externalProjections.TryGetValue(link.Id, out var projection)) continue;
                var playlist = await ReadCoreAsync(context, CreateProtocolId(link.Id), null, token, projection);
                if (playlist != null)
                    result.Add(playlist with
                    {
                        SummaryTrackCount = playlist.TrackCount,
                        SummaryDurationMilliseconds = playlist.DurationMilliseconds,
                        Tracks = []
                    });
                continue;
            }
            var count = await db.PlaylistSourceEntries.CountAsync(entry => entry.PlaylistSourceSnapshotId == snapshot.Id, token);
            var snapshotKey = snapshot.Id.ToString().ToUpperInvariant();
            var duration = await db.Database.SqlQuery<long>($"""
                SELECT CAST(COALESCE(SUM(COALESCE(CASE WHEN {link.ProjectionMode == PlaylistProjectionMode.Source} THEN NULL ELSE l."DurationMilliseconds" END,
                    NULLIF(json_extract(m."PayloadJson", '$.DurationMilliseconds'), 0),
                    NULLIF(json_extract(m."PayloadJson", '$.durationMilliseconds'), 0),
                    NULLIF(json_extract(m."PayloadJson", '$.durationSeconds'), 0) * 1000, l."DurationMilliseconds", r."DurationMilliseconds")), 0) AS INTEGER) AS "Value"
                FROM playlist_source_entries e JOIN external_metadata_snapshots m ON m."Id" = e."ExternalMetadataSnapshotId"
                LEFT JOIN track_matches t ON t."Id" = e."PublishedTrackMatchId"
                LEFT JOIN library_tracks l ON l."Id" = t."LibraryTrackId"
                LEFT JOIN provider_track_identities i ON i."Id" = m."ProviderTrackIdentityId"
                LEFT JOIN canonical_recordings r ON r."Id" = COALESCE(t."CanonicalRecordingId", i."CanonicalRecordingId", l."CanonicalRecordingId")
                WHERE e."PlaylistSourceSnapshotId" = {snapshotKey}
                """).SingleAsync(token);
            result.Add(new(CreateProtocolId(link.Id), link.Id, snapshot.Id,
                DisplayName(link.SourceProviderId, snapshot.Name), snapshot.Description, snapshot.ArtworkReferenceKey,
                link.SourceProviderId, link.SourcePlaylistId, snapshot.ProviderRevision, link.Mode, [], link.ProjectionMode, link.TargetPlaylistId)
            {
                CreatedAt = link.CreatedAt,
                UpdatedAt = snapshot.PublishedAt,
                SummaryTrackCount = count,
                SummaryDurationMilliseconds = duration
            });
        }
        if (personalMixes != null) result.AddRange(await personalMixes.PublishedAsync(context, token, summariesOnly: true));
        return result;
    }

    public static string DisplayName(string provider, string name)
    {
        if (provider.Equals("listenbrainz", StringComparison.OrdinalIgnoreCase))
            foreach (var title in new[] { "Weekly Jams", "Weekly Exploration" })
                if (name.Equals(title, StringComparison.OrdinalIgnoreCase) || name.StartsWith(title + " for ", StringComparison.OrdinalIgnoreCase)) return title;
        return name;
    }

    public Task<VirtualPlaylistReadModel?> ReadAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(context, protocolId, null, cancellationToken);

    public Task<VirtualPlaylistReadModel?> ReadAsync(
        ProtocolExecutionContext context,
        string protocolId,
        PlaylistProjectionMode projectionMode,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(context, protocolId, projectionMode, cancellationToken);

    private async Task<VirtualPlaylistReadModel?> ReadCoreAsync(
        ProtocolExecutionContext context,
        string protocolId,
        PlaylistProjectionMode? projectionMode,
        CancellationToken cancellationToken,
        DurablePlaylistProjection? publishedProjection = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var virtualId = TryParseProtocolId(protocolId, out var linkId);
        if (context.Actor == null || string.IsNullOrWhiteSpace(protocolId) ||
            !virtualId && context.Protocol != ProtocolKind.Jellyfin)
            return null;

        var actor = context.Actor;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var backendType = context.Protocol.ToString().ToLowerInvariant();
        if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actor.EffectiveUserId && user.Enabled &&
                user.BackendType == backendType && user.BackendInstanceId == context.BackendInstanceId &&
                user.BackendPrincipalId == context.VerifiedBackendPrincipalId, cancellationToken))
            return null;
        var links = await db.PlaylistLinks.AsNoTracking().Where(item =>
            (virtualId ? item.Id == linkId : item.TargetPlaylistId == protocolId) &&
            (item.OwnerUserId == actor.EffectiveUserId || item.TargetPlaylistId != null) &&
            item.TargetBackendInstanceId == context.BackendInstanceId &&
            (context.Protocol == ProtocolKind.Jellyfin
                ? item.TargetProtocol == "jellyfin"
                : item.TargetProtocol == "subsonic" || item.TargetProtocol == "opensubsonic" || item.TargetProtocol == "navidrome") &&
            item.Enabled && (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid ||
                context.Protocol == ProtocolKind.Jellyfin)).ToArrayAsync(cancellationToken);
        var owned = links.Where(item => item.OwnerUserId == actor.EffectiveUserId).ToArray();
        var selected = owned.Length > 0 ? owned : links;
        if (selected.Length == 0 && virtualId && personalMixes != null)
            return (await personalMixes.PublishedAsync(context, cancellationToken, linkId)).SingleOrDefault(item => item.LinkId == linkId);
        if (selected.Length != 1) return null;
        var link = selected[0];
        if (allstarr.Core.Music.PersonalMixService.IsExternalLink(link) &&
            (personalMixes == null || !await personalMixes.IsExternalVisibleAsync(context, link, cancellationToken))) return null;
        BackendPlaylistSnapshot? authorizedTarget = null;
        if (!string.IsNullOrWhiteSpace(link.TargetPlaylistId))
        {
            if (targets == null) return null;
            var read = await targets.Resolve(link.TargetProtocol).ReadAsync(
                new BackendPlaylistTargetContext(link.TargetBackendInstanceId, context.VerifiedBackendPrincipalId,
                    link.OwnerUserId == actor.EffectiveUserId ? link.TargetCredentialReferenceId?.ToString() : null), link.TargetPlaylistId, cancellationToken);
            if (!read.IsSuccess || read.Value == null) return null;
            authorizedTarget = read.Value;
        }
        else if (link.OwnerUserId != actor.EffectiveUserId) return null;

        var projection = publishedProjection ?? await projections.ReadByLinkIdAsync(
            link.OwnerUserId, link.Id, cancellationToken, actor.EffectiveUserId, includeHistory: false);
        if (projection == null) return null;
        var selectedMode = projectionMode ?? link.ProjectionMode;
        var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking()
            .SingleAsync(item => item.Id == projection.SnapshotId, cancellationToken);
        var targetSnapshot = selectedMode == PlaylistProjectionMode.Target ? authorizedTarget : null;
        if (selectedMode == PlaylistProjectionMode.Target && targetSnapshot == null) return null;
        var backendIds = projection.Entries
            .Where(item => item.RouteKind == "local" && item.BackendItemId != null)
            .Select(item => item.BackendItemId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var access = await libraryAccess.ResolveAsync(context, cancellationToken);
        var localCopies = await LibraryTrackAccess.Query(db, context, access)
            .Where(item => item.BackendInstanceId == link.TargetBackendInstanceId && backendIds.Contains(item.BackendItemId))
            .OrderBy(item => item.BackendLibraryId).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var libraryTracks = localCopies.DistinctBy(item => item.BackendItemId, StringComparer.Ordinal)
            .ToDictionary(item => item.BackendItemId, StringComparer.Ordinal);
        IReadOnlyDictionary<string, BackendPlaylistMember>? nativeItems = null;
        if (selectedMode == PlaylistProjectionMode.Resolved &&
            context.Protocol == ProtocolKind.Subsonic &&
            link.TargetProtocol is "subsonic" or "opensubsonic" or "navidrome" &&
            backendIds.Length > 0)
        {
            if (backendIds.Any(id => !libraryTracks.ContainsKey(id))) return null;
            if (targets == null) return null;
            var nativeResult = await targets.Resolve(link.TargetProtocol).ReadItemsAsync(
                new BackendPlaylistTargetContext(
                    link.TargetBackendInstanceId,
                    context.VerifiedBackendPrincipalId,
                    link.OwnerUserId == actor.EffectiveUserId ? link.TargetCredentialReferenceId?.ToString() : null),
                backendIds,
                cancellationToken);
            if (!nativeResult.IsSuccess || nativeResult.Value == null) return null;
            var hydrated = new Dictionary<string, BackendPlaylistMember>(StringComparer.Ordinal);
            foreach (var member in nativeResult.Value)
            {
                if (!hydrated.TryAdd(member.BackendItemId, member)) return null;
            }
            if (backendIds.Any(id => !hydrated.ContainsKey(id))) return null;
            nativeItems = hydrated;
        }
        var sourceByPosition = projection.SourceEntries.ToDictionary(item => item.Position);
        var resolvedTracks = projection.Entries
            .Select(item => ToResolvedVirtualTrack(item, libraryTracks, link.SourceProviderId, nativeItems,
                sourceByPosition.GetValueOrDefault(item.Position)?.Metadata))
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
            selectedMode,
            sourceTracks,
            resolvedTracks,
            targetTracks);
        if (tracks == null) return null;
        var name = targetSnapshot == null ? projection.Name : targetSnapshot.Name;
        var description = targetSnapshot == null ? projection.Description : targetSnapshot.Description;
        var artwork = targetSnapshot == null
            ? projection.ArtworkReferenceKey
            : targetSnapshot.ArtworkReference;
        if (context.Protocol == ProtocolKind.Jellyfin && authorizedTarget?.ArtworkReference is { } nativeArtwork)
            artwork = nativeArtwork;
        var result = new VirtualPlaylistReadModel(CreateProtocolId(link.Id), link.Id, projection.SnapshotId,
            DisplayName(link.SourceProviderId, name), description, artwork, link.SourceProviderId,
            link.SourcePlaylistId, snapshot.ProviderRevision, link.Mode, tracks, selectedMode,
            link.TargetPlaylistId)
        {
            CreatedAt = link.CreatedAt,
            UpdatedAt = snapshot.PublishedAt,
            BackendOwner = link.Mode == PlaylistLinkMode.Virtual ? null : authorizedTarget?.Owner,
            BackendPublic = link.Mode == PlaylistLinkMode.Virtual ? null : authorizedTarget?.IsPublic,
            MixCover = allstarr.Core.Music.PersonalMixService.IsExternalLink(link)
                ? new(link.Id.ToString(), snapshot.SnapshotVersion, "external", Source: link.SourceProviderId,
                    SourcePlaylistId: link.SourcePlaylistId, Name: DisplayName(link.SourceProviderId, name), Count: tracks.Count) : null
        };
        if (!allstarr.Core.Music.PersonalMixService.IsExternalLink(link) || personalMixes == null) return result;
        result = await personalMixes.FilterExternalTracksAsync(context, result, cancellationToken);
        var seen = new HashSet<(TrackRouteKind Kind, string? Provider, string Id)>();
        var uniqueTracks = result.Tracks.Where(track => track.RouteKind switch
        {
            TrackRouteKind.Local => seen.Add((track.RouteKind, null, track.BackendItemId)),
            TrackRouteKind.External when track.RouteProviderId != null && track.RouteExternalId != null =>
                seen.Add((track.RouteKind, track.RouteProviderId, track.RouteExternalId)),
            _ => true
        }).ToArray();
        return result with
        {
            Tracks = uniqueTracks,
            MixCover = result.MixCover is { } cover ? cover with { Count = uniqueTracks.Length } : null
        };
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
        var candidates = await db.PlaylistLinks.AsNoTracking()
            .Where(item =>
                (item.OwnerUserId == actor.EffectiveUserId || item.TargetPlaylistId != null) &&
                item.TargetBackendInstanceId == context.BackendInstanceId &&
                item.SourceProviderId == providerId &&
                item.SourcePlaylistId == playlistId &&
                (context.Protocol == ProtocolKind.Jellyfin
                    ? item.TargetProtocol == "jellyfin"
                    : item.TargetProtocol == "subsonic" ||
                      item.TargetProtocol == "opensubsonic" ||
                      item.TargetProtocol == "navidrome") &&
                item.Enabled &&
                (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid ||
                 context.Protocol == ProtocolKind.Jellyfin))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new { item.Id, item.OwnerUserId })
            .ToArrayAsync(cancellationToken);
        var owned = candidates.Where(item => item.OwnerUserId == actor.EffectiveUserId).ToArray();
        if (owned.Length > 1) return null;
        var selected = owned.Length > 0 ? owned : candidates;
        VirtualPlaylistReadModel? result = null;
        foreach (var candidate in selected)
        {
            var visible = await ReadAsync(context, CreateProtocolId(candidate.Id), cancellationToken);
            if (visible == null) continue;
            if (result != null) return null;
            result = visible;
        }
        return result;
    }

    internal static VirtualPlaylistTrack ToResolvedVirtualTrack(
        DurablePlaylistEntryProjection entry,
        IReadOnlyDictionary<string, LibraryTrackRecord> libraryTracks,
        string sourceProviderId,
        IReadOnlyDictionary<string, BackendPlaylistMember>? nativeItems = null,
        PlaylistSourceMetadata? sourceMetadata = null)
    {
        if (entry.BackendItemId != null &&
            libraryTracks.TryGetValue(entry.BackendItemId, out var local))
            return new(entry.Position, local.BackendItemId, local.Title, local.Artist,
                local.Album, local.AlbumArtist, local.DurationMilliseconds ?? entry.DurationMilliseconds,
                local.CoverArtReference, entry.MatchState ?? TrackMatchState.Unresolved,
                sourceProviderId, null, TrackRouteKind.Local,
                SourceMetadata: sourceMetadata,
                NativeEntryJson: nativeItems?.GetValueOrDefault(local.BackendItemId)?.NativeEntryJson,
                SourceEntryId: entry.SourceEntryId);
        var artist = entry.Artists.FirstOrDefault();
        if (entry.RouteKind == "external" && entry.RouteProviderId != null)
        {
            return new(entry.Position, $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}",
                entry.Title, string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
                entry.Album, null,
                entry.DurationMilliseconds / 1000 * 1000, null,
                entry.MatchState ?? TrackMatchState.Unresolved,
                entry.RouteProviderId, entry.ExternalId, TrackRouteKind.External,
                entry.RouteProviderId, entry.ExternalId, SourceMetadata: sourceMetadata, SourceEntryId: entry.SourceEntryId);
        }

        return new(entry.Position, $"{UnresolvedItemPrefix}{entry.ExternalId}",
            entry.Title, string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
            entry.Album, null,
            entry.DurationMilliseconds, null, entry.MatchState ?? TrackMatchState.Unresolved,
            sourceProviderId, null, TrackRouteKind.Unresolved, SourceMetadata: sourceMetadata, SourceEntryId: entry.SourceEntryId);
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
            metadata.DurationMilliseconds ?? resolved?.DurationMilliseconds,
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
            metadata,
            SourceEntryId: source.SourceEntryId);
    }
}
