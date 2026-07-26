using System.Text.Json;
using allstarr.Core.Capabilities;
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
    ITrackMatchRepository trackMatches,
    IProtocolProviderGateway? providerGateway = null) : IPlaylistVirtualizationService
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
            item.Enabled && (item.Mode == PlaylistLinkMode.Virtual || item.Mode == PlaylistLinkMode.Hybrid),
            cancellationToken);
        if (link == null || context.LibraryScopeId is { Length: > 0 } requestedLibrary &&
            !requestedLibrary.Equals(link.LibraryScopeId, StringComparison.Ordinal))
            return null;

        var snapshot = await db.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.PlaylistLinkId == link.Id &&
                           item.PublishedAt.HasValue)
            .OrderByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;

        var entries = await db.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderBy(item => item.SourcePosition)
            .ToListAsync(cancellationToken);
        var externalIds = entries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray();
        var resolution = await trackMatches.GetResolutionDataAsync(
            new TrackMatchActor(
                actor.TenantId,
                actor.EffectiveUserId ?? link.OwnerUserId,
                actor.Kind == ProviderActorKind.Administrator),
            link.OwnerUserId,
            link.LibraryScopeId,
            externalIds,
            cancellationToken);
        var externalSnapshots = resolution.Snapshots.ToDictionary(item => item.Id);
        var providerIdentities = resolution.ProviderIdentities.ToDictionary(item => item.Id);
        var providerOrder = providerGateway?.GetProviderOrder(ProviderCapabilityKind.Streaming) ??
                            resolution.ProviderIdentities.Select(item => item.ProviderId)
                                .Distinct(StringComparer.Ordinal)
                                .Order(StringComparer.Ordinal)
                                .ToArray();
        var overrides = resolution.ActiveOverrides.ToDictionary(item => item.ExternalSnapshotId);
        var publishedMatchIds = entries
            .Where(item => item.PublishedTrackMatchId.HasValue)
            .Select(item => item.PublishedTrackMatchId!.Value)
            .Distinct()
            .ToArray();
        var publishedMatches = await db.TrackMatches.AsNoTracking()
            .Where(item => publishedMatchIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var selected = entries.Select(entry =>
        {
            overrides.TryGetValue(entry.ExternalMetadataSnapshotId, out var manual);
            TrackMatchRecord? decision = null;
            if (entry.PublishedTrackMatchId is { } matchId)
                publishedMatches.TryGetValue(matchId, out decision);
            var rejected = TrackMatchOverridePolicy.IsEffectiveRejection(manual, decision);
            var state = manual?.Decision == ManualOverrideDecision.Pin
                ? TrackMatchState.Pinned
                : rejected
                    ? TrackMatchState.Rejected
                    : decision?.State ?? TrackMatchState.Unresolved;
            var trackId = manual?.Decision == ManualOverrideDecision.Pin
                ? manual.LibraryTrackId
                : state == TrackMatchState.Accepted && decision!.Confidence >= decision.Threshold
                    ? decision.LibraryTrackId
                    : null;
            ProviderTrackIdentityRecord? providerIdentity = null;
            if (!trackId.HasValue && externalSnapshots.TryGetValue(entry.ExternalMetadataSnapshotId, out var external) &&
                external.ProviderTrackIdentityId.HasValue)
            {
                providerIdentities.TryGetValue(external.ProviderTrackIdentityId.Value, out var sourceIdentity);
                var primary = DurableProviderRouteSelector.Select(
                    sourceIdentity, resolution.ProviderIdentities, providerOrder).FirstOrDefault();
                if (primary != null)
                    providerIdentity = resolution.ProviderIdentities.First(item =>
                        item.ProviderId == primary.ProviderId &&
                        item.ExternalId == primary.ExternalId);
            }
            return (Entry: entry, State: state, TrackId: trackId, ProviderIdentity: providerIdentity);
        }).Where(item => item.TrackId.HasValue || item.ProviderIdentity != null).ToList();
        var libraryIds = selected.Where(item => item.TrackId.HasValue).Select(item => item.TrackId!.Value).Distinct().ToArray();
        var libraryTracks = await db.LibraryTracks.AsNoTracking().Where(item =>
                libraryIds.Contains(item.Id) && item.TenantId == actor.TenantId &&
                item.OwnerUserId == link.OwnerUserId && item.LibraryScopeId == link.LibraryScopeId &&
                item.BackendInstanceId == link.TargetBackendInstanceId &&
                (context.Protocol == ProtocolKind.Jellyfin
                    ? item.Protocol == "jellyfin"
                    : item.Protocol == "subsonic" || item.Protocol == "opensubsonic" || item.Protocol == "navidrome"))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var tracks = selected
            .Select(item =>
            {
                if (item.TrackId.HasValue && libraryTracks.TryGetValue(item.TrackId.Value, out var track))
                    return new VirtualPlaylistTrack(item.Entry.SourcePosition, track.BackendItemId,
                        track.Title, track.Artist, track.Album, track.AlbumArtist,
                        track.DurationMilliseconds, track.CoverArtReference, item.State);

                if (item.ProviderIdentity != null &&
                    externalSnapshots.TryGetValue(item.Entry.ExternalMetadataSnapshotId, out var external))
                    return ToProviderTrack(item.Entry.SourcePosition, item.ProviderIdentity, external, item.State);

                return null;
            })
            .Where(item => item != null)
            .Select(item => item!)
            .ToList();
        return new VirtualPlaylistReadModel(protocolId, link.Id, snapshot.Id, snapshot.Name,
            snapshot.Description, snapshot.ArtworkReferenceKey, link.SourceProviderId,
            snapshot.ProviderRevision, link.Mode, tracks);
    }

    private static VirtualPlaylistTrack? ToProviderTrack(
        int position,
        ProviderTrackIdentityRecord identity,
        ExternalMetadataSnapshotRecord snapshot,
        TrackMatchState state)
    {
        try
        {
            using var document = JsonDocument.Parse(snapshot.PayloadJson);
            var root = document.RootElement;
            var title = Text(root, "Title") ?? Text(root, "title");
            var artists = Array(root, "Artists");
            if (artists.Count == 0) artists = Array(root, "artists");
            var artist = artists.FirstOrDefault() ?? Text(root, "Artist") ?? Text(root, "artist");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
            var durationMilliseconds = Number(root, "durationMilliseconds") ??
                                       Number(root, "DurationMilliseconds") ??
                                       (Number(root, "durationSeconds") ?? Number(root, "DurationSeconds")) * 1000d;
            return new VirtualPlaylistTrack(
                position,
                $"ext-{identity.ProviderId}-song-{identity.ExternalId}",
                title,
                artist,
                Text(root, "Album") ?? Text(root, "album"),
                Text(root, "AlbumArtist") ?? Text(root, "albumArtist"),
                durationMilliseconds.HasValue ? checked((long)Math.Round(durationMilliseconds.Value)) : null,
                null,
                state);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
}
