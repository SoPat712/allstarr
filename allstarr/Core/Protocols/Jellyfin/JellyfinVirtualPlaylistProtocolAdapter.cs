using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Protocols.Jellyfin;

public sealed record JellyfinPlaylistMutationRoute(bool Writable, string? TargetPlaylistId);

public interface IJellyfinPlaylistMutationResolver
{
    Task<JellyfinPlaylistMutationRoute?> ResolveAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default);
}

public sealed class JellyfinPlaylistMutationResolver(
    IDbContextFactory<AllstarrDbContext> contextFactory) : IJellyfinPlaylistMutationResolver
{
    public async Task<JellyfinPlaylistMutationRoute?> ResolveAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Protocol != ProtocolKind.Jellyfin ||
            !PlaylistVirtualizationService.TryParseProtocolId(protocolId, out var linkId) ||
            context.Actor?.EffectiveUserId is not { } userId)
        {
            return null;
        }

        var actor = context.Actor;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == linkId &&
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == userId &&
            item.TargetBackendInstanceId == context.BackendInstanceId &&
            item.TargetProtocol == "jellyfin" &&
            item.Enabled &&
            (context.LibraryScopeId == null || item.LibraryScopeId == context.LibraryScopeId),
            cancellationToken);
        if (link == null) return null;

        var writable = link.Mode != PlaylistLinkMode.Virtual &&
                       !string.IsNullOrWhiteSpace(link.TargetPlaylistId);
        return new JellyfinPlaylistMutationRoute(
            writable,
            writable ? link.TargetPlaylistId!.Trim() : null);
    }
}

public sealed class JellyfinVirtualPlaylistProtocolAdapter(
    IPlaylistVirtualizationService playlists,
    IJellyfinPlaylistMutationResolver mutationResolver,
    IOptions<JellyfinSettings>? settings = null,
    JellyfinProxyService? proxyService = null,
    JellyfinResponseBuilder? responseBuilder = null,
    ILogger<JellyfinVirtualPlaylistProtocolAdapter>? logger = null,
    IProtocolProviderGateway? providerGateway = null)
{
    private const string FullItemFields =
        "AirTime,CanDelete,CanDownload,ChannelInfo,Chapters,Trickplay,ChildCount," +
        "CumulativeRunTimeTicks,CustomRating,DateCreated,DateLastMediaAdded,DisplayPreferencesId," +
        "Etag,ExternalUrls,Genres,ItemCounts,MediaSourceCount,MediaSources,OriginalTitle,Overview," +
        "ParentId,Path,People,PlayAccess,ProductionLocations,ProviderIds,PrimaryImageAspectRatio," +
        "RecursiveItemCount,Settings,SeriesStudio,SortName,SpecialEpisodeNumbers,Studios,Taglines," +
        "Tags,RemoteTrailers,MediaStreams,SeasonUserData,DateLastRefreshed,DateLastSaved,RefreshState," +
        "ChannelImage,EnableMediaSourceDisplay,Width,Height,ExtraIds,LocalTrailerCount,IsHD," +
        "SpecialFeatureCount";

    private readonly string serverId = string.IsNullOrWhiteSpace(settings?.Value.DeviceId)
        ? "allstarrrr-proxy"
        : settings.Value.DeviceId;

    public bool IsVirtualPlaylistId(string? value) =>
        PlaylistVirtualizationService.TryParseProtocolId(value, out _);

    public Task<JellyfinPlaylistMutationRoute?> ResolveMutationAsync(
        ProtocolExecutionContext context,
        string id,
        CancellationToken cancellationToken) =>
        mutationResolver.ResolveAsync(context, id, cancellationToken);

    public Task<IReadOnlyList<VirtualPlaylistReadModel>> ListAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken) =>
        playlists.ListAsync(context, cancellationToken);

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListItemsAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var visible = await ListAsync(context, cancellationToken);
        return visible.Select(item => ToItem(item)).ToArray();
    }

    public async Task<IActionResult?> ReadItemAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        return new JsonResult(ToItem(playlist));
    }

    public async Task<IActionResult?> ReadItemBySourceAsync(
        ProtocolExecutionContext context,
        string sourceProviderId,
        string sourcePlaylistId,
        string responsePlaylistId,
        CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadBySourceAsync(
            context, sourceProviderId, sourcePlaylistId, cancellationToken);
        return playlist == null ? null : new JsonResult(ToItem(playlist, responsePlaylistId));
    }

    public async Task<IActionResult?> ReadDefinitionAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        return playlist == null
            ? null
            : new JsonResult(new
            {
                OpenAccess = false,
                Shares = Array.Empty<object>(),
                ItemIds = playlist.Tracks.Select(track => track.BackendItemId).ToArray()
            });
    }

    public async Task<IActionResult?> ReadDefinitionBySourceAsync(
        ProtocolExecutionContext context,
        string sourceProviderId,
        string sourcePlaylistId,
        CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadBySourceAsync(
            context, sourceProviderId, sourcePlaylistId, cancellationToken);
        return playlist == null
            ? null
            : new JsonResult(new
            {
                OpenAccess = false,
                Shares = Array.Empty<object>(),
                ItemIds = playlist.Tracks.Select(track => track.BackendItemId).ToArray()
            });
    }

    public async Task<string?> GetImageSourceIdAsync(
        ProtocolExecutionContext? context, string id, CancellationToken cancellationToken)
    {
        if (context != null)
        {
            var playlist = await playlists.ReadAsync(context, id, cancellationToken);
            return playlist?.ArtworkReferenceKey == null
                ? null
                : playlist.TargetPlaylistId ??
                  PlaylistIdHelper.CreatePlaylistId(playlist.SourceProviderId, playlist.SourcePlaylistId);
        }

        var source = await playlists.ResolvePublicArtworkSourceAsync(id, cancellationToken);
        return source == null
            ? null
            : source.TargetPlaylistId ??
              PlaylistIdHelper.CreatePlaylistId(source.ProviderId, source.PlaylistId);
    }

    internal Dictionary<string, object?> ToItem(
        VirtualPlaylistReadModel playlist,
        string? responsePlaylistId = null)
    {
        var id = responsePlaylistId ?? playlist.ProtocolId;
        return new()
        {
            ["Name"] = playlist.Name,
            ["Overview"] = playlist.Description,
            ["ServerId"] = serverId,
            ["Id"] = id,
            ["IsFolder"] = true,
            ["Type"] = "Playlist",
            ["MediaType"] = "Audio",
            ["ChildCount"] = playlist.Tracks.Count,
            ["RunTimeTicks"] = playlist.Tracks.Sum(track => track.DurationMilliseconds) * TimeSpan.TicksPerMillisecond,
            ["UserData"] = UserData(id),
            ["ImageTags"] = playlist.ArtworkReferenceKey == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["Primary"] = playlist.ArtworkReferenceKey },
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.SourceProviderId] = playlist.SourceRevision
            }
        };
    }

    public Task<IActionResult?> ReadItemsAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken) =>
        ReadItemsAsync(context, id, null, null, cancellationToken);

    public async Task<IActionResult?> ReadItemsAsync(
        ProtocolExecutionContext context,
        string id,
        IHeaderDictionary? clientHeaders,
        IQueryCollection? clientQuery,
        CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        return await CreateItemsResponseAsync(
            context, playlist, id, clientHeaders, clientQuery);
    }

    public async Task<IActionResult?> ReadItemsBySourceAsync(
        ProtocolExecutionContext context,
        string sourceProviderId,
        string sourcePlaylistId,
        string responsePlaylistId,
        IHeaderDictionary? clientHeaders,
        IQueryCollection? clientQuery,
        CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadBySourceAsync(
            context, sourceProviderId, sourcePlaylistId, cancellationToken);
        return playlist == null
            ? null
            : await CreateItemsResponseAsync(
                context, playlist, responsePlaylistId, clientHeaders, clientQuery);
    }

    private async Task<IActionResult> CreateItemsResponseAsync(
        ProtocolExecutionContext context,
        VirtualPlaylistReadModel playlist,
        string responsePlaylistId,
        IHeaderDictionary? clientHeaders,
        IQueryCollection? clientQuery)
    {
        var startIndex = QueryInt(clientQuery, "StartIndex", 0);
        var limit = QueryInt(clientQuery, "Limit", int.MaxValue);
        var tracks = playlist.Tracks.Skip(startIndex).Take(limit).ToArray();
        var originals = playlist.ProjectionMode is not (PlaylistProjectionMode.Resolved or PlaylistProjectionMode.Target) || proxyService == null
            ? null
            : await ReadOriginalItemsAsync(context, tracks, clientHeaders, clientQuery);
        IReadOnlyDictionary<string, Song> externalSongs = responseBuilder == null
            ? new Dictionary<string, Song>()
            : await ReadExternalSongsAsync(context, tracks);
        var items = tracks.Select(track =>
        {
            JsonObject item;
            if (playlist.ProjectionMode is PlaylistProjectionMode.Resolved or PlaylistProjectionMode.Target &&
                track.RouteKind == TrackRouteKind.Local && originals != null)
            {
                if (!originals.TryGetValue(track.BackendItemId, out var original))
                {
                    logger?.LogWarning(
                        "Jellyfin no longer returns matched playlist item {BackendItemId}; serving it as unresolved",
                        track.BackendItemId);
                    item = JsonSerializer.SerializeToNode(FallbackItem(track))!.AsObject();
                    item["Id"] = $"{PlaylistVirtualizationService.UnresolvedItemPrefix}{track.BackendItemId}";
                    AddSourceLabels(item, track.SourceProviderId);
                    item["LocationType"] = "Virtual";
                    item["PlayAccess"] = "None";
                    item["CanDownload"] = false;
                    item["CanDelete"] = false;
                    item["SupportsSync"] = false;
                    item["HasLyrics"] = false;
                    item["MediaSources"] = new JsonArray();
                }
                else
                {
                    item = (JsonObject)original.DeepClone();
                }
            }
            else if (playlist.ProjectionMode == PlaylistProjectionMode.Resolved &&
                     track.RouteKind == TrackRouteKind.External && responseBuilder != null)
            {
                var song = externalSongs.GetValueOrDefault(track.BackendItemId) ?? new Song
                {
                    Id = track.BackendItemId,
                    Title = track.Title,
                    Artist = track.Artist,
                    Artists = [track.Artist],
                    Album = track.Album ?? string.Empty,
                    AlbumArtist = track.AlbumArtist,
                    Duration = track.DurationMilliseconds is { } milliseconds
                        ? checked((int)(milliseconds / 1000))
                        : null,
                    IsLocal = false,
                    ExternalProvider = track.RouteProviderId ?? track.SourceProviderId,
                    ExternalId = track.RouteExternalId ?? track.SourceExternalId
                };
                item = JsonSerializer.SerializeToNode(responseBuilder.ConvertSongToJellyfinItem(song))!.AsObject();
            }
            else
            {
                item = JsonSerializer.SerializeToNode(FallbackItem(track))!.AsObject();
                if (playlist.ProjectionMode == PlaylistProjectionMode.Source ||
                    track.RouteKind != TrackRouteKind.Local)
                {
                    var labelProvider = playlist.ProjectionMode == PlaylistProjectionMode.Source
                        ? track.SourceProviderId
                        : track.RouteProviderId ?? track.SourceProviderId;
                    AddSourceLabels(item, labelProvider);
                }
                if (playlist.ProjectionMode == PlaylistProjectionMode.Source)
                {
                    AddSourceIdentity(item, track);
                    item["LocationType"] = "Virtual";
                    item["MediaSources"] = new JsonArray();
                }
                if (track.RouteKind == TrackRouteKind.Unresolved)
                {
                    item["LocationType"] = "Virtual";
                    item["PlayAccess"] = "None";
                    item["CanDownload"] = false;
                    item["CanDelete"] = false;
                    item["SupportsSync"] = false;
                    item["HasLyrics"] = false;
                    item["MediaSources"] = new JsonArray();
                }
            }

            if (track.RouteKind == TrackRouteKind.Local &&
                !string.IsNullOrWhiteSpace(track.SourceProviderId))
            {
                var providerIds = item["ProviderIds"] as JsonObject ?? [];
                item["ProviderIds"] = providerIds;
                providerIds["AllstarrSource"] = track.SourceProviderId;
            }
            item["ParentId"] = responsePlaylistId;
            item["PlaylistItemId"] = track.NativePlaylistEntryId ?? track.BackendItemId;
            return item;
        }).ToArray();
        logger?.LogInformation(
            "Served injected playlist projection {ResponsePlaylistId} from {ProtocolId}: total={TotalCount}, start={StartIndex}, returned={ReturnedCount}",
            responsePlaylistId,
            playlist.ProtocolId,
            playlist.Tracks.Count,
            startIndex,
            items.Length);
        return new JsonResult(new
        {
            Items = items,
            TotalRecordCount = playlist.Tracks.Count,
            StartIndex = startIndex
        });
    }

    private async Task<IReadOnlyDictionary<string, Song>> ReadExternalSongsAsync(
        ProtocolExecutionContext context,
        IReadOnlyCollection<VirtualPlaylistTrack> tracks)
    {
        var result = new Dictionary<string, Song>(StringComparer.Ordinal);
        if (providerGateway == null) return result;

        var external = tracks
            .Where(track => track.RouteKind == TrackRouteKind.External &&
                            !string.IsNullOrWhiteSpace(track.RouteProviderId) &&
                            !string.IsNullOrWhiteSpace(track.RouteExternalId))
            .DistinctBy(track => track.BackendItemId)
            .ToArray();
        foreach (var batch in external.Chunk(4))
        {
            var songs = await Task.WhenAll(batch.Select(async track =>
            {
                try
                {
                    return (track.BackendItemId, Song: await providerGateway.GetSongAsync(
                        context, track.RouteProviderId!, track.RouteExternalId!));
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception,
                        "Could not hydrate a {ProviderId} virtual playlist track", track.RouteProviderId);
                    return (track.BackendItemId, Song: (Song?)null);
                }
            }));
            foreach (var (id, song) in songs)
                if (song != null) result[id] = song;
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<string, JsonObject>> ReadOriginalItemsAsync(
        ProtocolExecutionContext context,
        IReadOnlyList<VirtualPlaylistTrack> tracks,
        IHeaderDictionary? clientHeaders,
        IQueryCollection? clientQuery)
    {
        var ids = tracks
            .Where(track => track.RouteKind == TrackRouteKind.Local)
            .Select(track => track.BackendItemId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return new Dictionary<string, JsonObject>();

        var batches = await Task.WhenAll(ids.Chunk(100).Select(async batch =>
        {
            var query = new Dictionary<string, string>
            {
                ["Ids"] = string.Join(',', batch),
                ["UserId"] = context.VerifiedBackendPrincipalId,
                ["Recursive"] = "true",
                ["EnableImages"] = "true",
                ["EnableUserData"] = "true",
                ["Fields"] = FullItemFields,
                ["Limit"] = batch.Length.ToString()
            };
            foreach (var name in new[] { "api_key", "access_token", "ApiKey" })
                if (clientQuery?.TryGetValue(name, out var value) == true && !string.IsNullOrWhiteSpace(value))
                    query[name] = value.ToString();

            var (body, statusCode) = await proxyService!.GetJsonAsync("Items", query, clientHeaders);
            using (body)
            {
                if (statusCode is < 200 or >= 300 || body == null ||
                    !body.RootElement.TryGetProperty("Items", out var items))
                    throw new InvalidOperationException(
                        $"Jellyfin item hydration failed with status {statusCode}.");
                return items.EnumerateArray()
                    .Select(item => JsonNode.Parse(item.GetRawText())!.AsObject())
                    .ToArray();
            }
        }));

        return batches.SelectMany(batch => batch)
            .Where(item => item["Id"] is JsonValue)
            .ToDictionary(item => item["Id"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    private static void AddSourceLabels(JsonObject item, string? provider)
    {
        foreach (var name in new[] { "Name", "Album", "AlbumArtist" })
            Label(item, name, provider);
        if (item["Artists"] is JsonArray artists)
            for (var index = 0; index < artists.Count; index++)
                if (artists[index] is JsonValue value && value.TryGetValue<string>(out var artist))
                    artists[index] = JellyfinResponseBuilder.AppendExternalSourceLabel(artist, provider);
        foreach (var collectionName in new[] { "ArtistItems", "AlbumArtists" })
            if (item[collectionName] is JsonArray values)
                foreach (var value in values.OfType<JsonObject>())
                    Label(value, "Name", provider);

        if (!string.IsNullOrWhiteSpace(provider))
        {
            var providerIds = item["ProviderIds"] as JsonObject ?? [];
            item["ProviderIds"] = providerIds;
            providerIds["AllstarrSource"] = provider;
        }
    }

    private static void AddSourceIdentity(JsonObject item, VirtualPlaylistTrack track)
    {
        if (track.SourceIdentity is not { } identity) return;
        var providerIds = item["ProviderIds"] as JsonObject ?? [];
        item["ProviderIds"] = providerIds;
        providerIds["AllstarrSourceHash"] = identity.ExternalIdHash;
        providerIds["AllstarrSourceRevision"] = identity.SourceRevision;
        if (identity.ExternalId != null) providerIds[identity.ProviderId] = identity.ExternalId;
        if (track.SourceMetadata?.Isrc != null) providerIds["ISRC"] = track.SourceMetadata.Isrc;
    }

    private static void Label(JsonObject item, string name, string? provider)
    {
        if (item[name] is JsonValue value && value.TryGetValue<string>(out var text))
            item[name] = JellyfinResponseBuilder.AppendExternalSourceLabel(text, provider);
    }

    private Dictionary<string, object?> FallbackItem(VirtualPlaylistTrack track) => new()
    {
        ["Name"] = track.Title,
        ["ServerId"] = serverId,
        ["Id"] = track.BackendItemId,
        ["Album"] = track.Album,
        ["AlbumArtist"] = track.AlbumArtist,
        ["Artists"] = track.SourceMetadata?.Artists is { Count: > 0 } artists
            ? artists
            : [track.Artist],
        ["RunTimeTicks"] = track.DurationMilliseconds * TimeSpan.TicksPerMillisecond,
        ["IndexNumber"] = track.SourcePosition + 1,
        ["IsFolder"] = false,
        ["Type"] = "Audio",
        ["MediaType"] = "Audio",
        ["LocationType"] = "FileSystem",
        ["UserData"] = UserData(track.BackendItemId),
        ["ImageTags"] = track.CoverArtReference == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Primary"] = track.CoverArtReference }
    };

    private static int QueryInt(IQueryCollection? query, string name, int fallback) =>
        query?.TryGetValue(name, out var value) == true &&
        int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : fallback;

    private static Dictionary<string, object> UserData(string id) => new()
    {
        ["ItemId"] = id,
        ["Key"] = id,
        ["PlaybackPositionTicks"] = 0L,
        ["PlayCount"] = 0,
        ["IsFavorite"] = false,
        ["Played"] = false
    };
}
