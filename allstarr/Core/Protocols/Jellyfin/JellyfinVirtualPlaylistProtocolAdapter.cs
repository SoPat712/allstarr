using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Matching;
using allstarr.Core.Playback;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IBackendPlaylistTargetResolver? targets = null) : IJellyfinPlaylistMutationResolver
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
            (item.OwnerUserId == userId || item.TargetPlaylistId != null) &&
            item.TargetBackendInstanceId == context.BackendInstanceId &&
            item.TargetProtocol == "jellyfin" &&
            item.Enabled,
            cancellationToken);
        if (link == null) return null;

        var writable = link.Mode != PlaylistLinkMode.Virtual &&
                       !string.IsNullOrWhiteSpace(link.TargetPlaylistId);
        if (!string.IsNullOrWhiteSpace(link.TargetPlaylistId))
        {
            if (targets == null) return null;
            var target = targets.Resolve(link.TargetProtocol);
            var targetContext = new BackendPlaylistTargetContext(context.BackendInstanceId,
                context.VerifiedBackendPrincipalId);
            var read = await target.ReadAsync(targetContext, link.TargetPlaylistId!, cancellationToken);
            if (!read.IsSuccess || read.Value == null) return null;
            writable = writable && await target.CanWriteAsync(targetContext, link.TargetPlaylistId!, cancellationToken);
        }
        else if (link.OwnerUserId != userId) return null;
        return new JellyfinPlaylistMutationRoute(
            writable,
            writable ? link.TargetPlaylistId!.Trim() : null);
    }
}

public sealed class JellyfinVirtualPlaylistProtocolAdapter(
    IPlaylistVirtualizationService playlists,
    IJellyfinPlaylistMutationResolver mutationResolver,
    JellyfinProxyService? proxyService = null,
    JellyfinResponseBuilder? responseBuilder = null,
    ILogger<JellyfinVirtualPlaylistProtocolAdapter>? logger = null,
    IProtocolProviderGateway? providerGateway = null,
    IHttpContextAccessor? httpContexts = null,
    UpcomingSongPreparation? upcomingSongs = null)
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

    private readonly JellyfinResponseBuilder _builder = responseBuilder ?? new JellyfinResponseBuilder(httpContexts: httpContexts);

    public bool IsVirtualPlaylistId(string? value) =>
        PlaylistVirtualizationService.TryParseProtocolId(value, out _);

    public async Task<string?> GetTargetPlaylistIdAsync(ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        return playlist?.Mode is PlaylistLinkMode.Materialized or PlaylistLinkMode.Hybrid ? playlist.TargetPlaylistId : null;
    }

    public Task<JellyfinPlaylistMutationRoute?> ResolveMutationAsync(
        ProtocolExecutionContext context,
        string id,
        CancellationToken cancellationToken) =>
        mutationResolver.ResolveAsync(context, id, cancellationToken);

    public async Task<IReadOnlyList<VirtualPlaylistReadModel>> ListAsync(
        ProtocolExecutionContext context, CancellationToken cancellationToken) =>
        (await playlists.ListAsync(context, cancellationToken))
        .Select(item => ExternalTrackPresentation.ApplyPreferences(item, context.Policy)).ToArray();

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListItemsAsync(
        ProtocolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var visible = await ListAsync(context, cancellationToken);
        var serverId = visible.Count == 0 ? null : await ServerIdAsync(context);
        return visible.Select(item => ToItem(item, serverId: serverId)).ToArray();
    }

    public async Task<IActionResult?> ReadItemAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist != null) playlist = ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
        if (playlist == null) return null;
        return new JsonResult(ToItem(playlist, id, await ServerIdAsync(context), includeAllFields: true));
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
        if (playlist != null) playlist = ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
        return playlist == null ? null : new JsonResult(ToItem(playlist, responsePlaylistId, await ServerIdAsync(context), includeAllFields: true));
    }

    public async Task<IActionResult?> ReadDefinitionAsync(
        ProtocolExecutionContext context, string id, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist != null) playlist = ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
        return playlist == null ? null : await DefinitionAsync(context, playlist, cancellationToken);
    }

    public async Task<IActionResult?> ReadDefinitionBySourceAsync(
        ProtocolExecutionContext context,
        string sourceProviderId,
        string sourcePlaylistId,
        CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadBySourceAsync(
            context, sourceProviderId, sourcePlaylistId, cancellationToken);
        if (playlist != null) playlist = ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
        return playlist == null ? null : await DefinitionAsync(context, playlist, cancellationToken);
    }

    private async Task<IActionResult> DefinitionAsync(ProtocolExecutionContext context, VirtualPlaylistReadModel playlist,
        CancellationToken cancellationToken)
    {
        JsonObject definition = new() { ["OpenAccess"] = false, ["Shares"] = new JsonArray() };
        if (playlist.Mode is PlaylistLinkMode.Materialized or PlaylistLinkMode.Hybrid &&
            playlist.TargetPlaylistId != null && proxyService != null)
        {
            var (native, status) = await proxyService.GetJsonAsync($"Playlists/{Uri.EscapeDataString(playlist.TargetPlaylistId)}",
                clientHeaders: httpContexts?.HttpContext?.Request.Headers, cancellationToken: cancellationToken);
            using (native)
            {
                if (status is < 200 or >= 300) return new StatusCodeResult(status);
                if (native != null && JsonNode.Parse(native.RootElement.GetRawText()) is JsonObject body && body["Shares"] is JsonArray)
                    definition = body;
            }
        }
        definition["ItemIds"] = new JsonArray(playlist.Tracks.Select(track => (JsonNode?)JsonValue.Create(track.BackendItemId)).ToArray());
        return new JsonResult(definition);
    }

    public async Task<string?> GetImageSourceIdAsync(
        ProtocolExecutionContext? context, string id, CancellationToken cancellationToken)
    {
        if (context != null)
        {
            var playlist = await playlists.ReadAsync(context, id, cancellationToken);
            if (playlist != null) playlist = ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
            return playlist?.ArtworkReferenceKey == null
                ? null
                : playlist.TargetPlaylistId ??
                  PlaylistIdHelper.CreatePlaylistId(playlist.SourceProviderId, playlist.SourcePlaylistId);
        }

        return null;
    }

    internal Dictionary<string, object?> ToItem(
        VirtualPlaylistReadModel playlist,
        string? responsePlaylistId = null,
        string? serverId = null,
        bool includeAllFields = false)
    {
        var id = responsePlaylistId ?? playlist.ProtocolId;
        var item = _builder.ConvertPlaylistToJellyfinItem(new ExternalPlaylist
        {
            Id = id,
            Name = playlist.Name,
            Description = playlist.Description,
            Provider = playlist.SourceProviderId,
            ExternalId = playlist.SourcePlaylistId,
            TrackCount = playlist.Tracks.Count
        }, httpContexts?.HttpContext?.Request.Query, includeAllFields);
        if (serverId != null) item["ServerId"] = serverId;
        item["RunTimeTicks"] = playlist.Tracks.Sum(track => track.DurationMilliseconds) * TimeSpan.TicksPerMillisecond;
        item["ImageTags"] = playlist.ArtworkReferenceKey == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Primary"] = playlist.ArtworkReferenceKey };
        return item;
    }

    private async Task<string?> ServerIdAsync(ProtocolExecutionContext context) =>
        proxyService == null ? null : await proxyService.GetServerIdAsync(context.BackendInstanceId, context.CancellationToken);

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
        if (playlist != null) playlist = ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
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
        if (playlist != null) playlist = ExternalTrackPresentation.ApplyPreferences(playlist, context.Policy);
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
        upcomingSongs?.RememberPlaylist(context, playlist.Tracks.Select(track => track.BackendItemId));
        clientQuery ??= httpContexts?.HttpContext?.Request.Query;
        var startIndex = QueryInt(clientQuery, "StartIndex", 0);
        var limit = QueryInt(clientQuery, "Limit", int.MaxValue);
        var tracks = playlist.Tracks.Skip(startIndex).Take(limit).ToArray();
        var originals = playlist.ProjectionMode is not (PlaylistProjectionMode.Resolved or PlaylistProjectionMode.Target)
            ? null
            : await ReadOriginalItemsAsync(context, tracks, clientHeaders, clientQuery);
        IReadOnlyDictionary<string, Song> externalSongs = responseBuilder == null
            ? new Dictionary<string, Song>()
            : await ReadExternalSongsAsync(context, tracks);
        var hasSyntheticItems = playlist.ProjectionMode == PlaylistProjectionMode.Source || tracks.Any(track =>
            track.RouteKind != TrackRouteKind.Local || originals?.ContainsKey(track.BackendItemId) != true);
        if (hasSyntheticItems && originals != null && proxyService != null)
        {
            var nativeServerId = originals.Values.Select(item => item["ServerId"]?.GetValue<string>())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (nativeServerId != null) proxyService.RememberServerId(context.BackendInstanceId, nativeServerId);
        }
        var serverId = hasSyntheticItems ? await ServerIdAsync(context) : null;
        var items = tracks.Select(track =>
        {
            JsonObject item;
            var native = false;
            if (playlist.ProjectionMode is PlaylistProjectionMode.Resolved or PlaylistProjectionMode.Target &&
                track.RouteKind == TrackRouteKind.Local && originals != null &&
                (proxyService != null || track.NativeEntryJson != null))
            {
                var original = track.NativeEntryJson == null
                    ? originals.GetValueOrDefault(track.BackendItemId)
                    : JsonNode.Parse(track.NativeEntryJson) as JsonObject;
                if (original == null)
                {
                    logger?.LogWarning(
                        "Jellyfin no longer returns matched playlist item {BackendItemId}; serving it as unresolved",
                        track.BackendItemId);
                    item = JsonSerializer.SerializeToNode(FallbackItem(track, clientQuery))!.AsObject();
                    item["Id"] = $"{PlaylistVirtualizationService.UnresolvedItemPrefix}{track.BackendItemId}";
                    AddSourceLabels(item, track, track.SourceProviderId, context.Policy?.Preferences);
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
                    native = true;
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
                    ExternalId = track.RouteExternalId ?? track.SourceExternalId,
                    ExplicitContentLyrics = track.SourceMetadata?.IsExplicit is { } explicitContent ? explicitContent ? 1 : 0 : null
                };
                item = JsonSerializer.SerializeToNode(_builder.ConvertSongToJellyfinItem(song, context.Policy?.Preferences, clientQuery))!.AsObject();
            }
            else
            {
                item = JsonSerializer.SerializeToNode(FallbackItem(track, clientQuery))!.AsObject();
                if (playlist.ProjectionMode == PlaylistProjectionMode.Source ||
                    track.RouteKind != TrackRouteKind.Local)
                {
                    var labelProvider = playlist.ProjectionMode == PlaylistProjectionMode.Source
                        ? track.SourceProviderId
                        : track.RouteProviderId ?? track.SourceProviderId;
                    AddSourceLabels(item, track, labelProvider, context.Policy?.Preferences);
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

            if (!native && track.RouteKind == TrackRouteKind.Local &&
                !string.IsNullOrWhiteSpace(track.SourceProviderId))
            {
                var providerIds = item["ProviderIds"] as JsonObject ?? [];
                item["ProviderIds"] = providerIds;
                providerIds["AllstarrSource"] = track.SourceProviderId;
            }
            if (!native)
            {
                item["ParentId"] = responsePlaylistId;
                if (serverId != null) item["ServerId"] = serverId;
                new JellyfinItemFields(clientQuery).Apply(item);
            }
            item["PlaylistItemId"] = track.NativePlaylistEntryId ??
                (track.SourceEntryId is { } sourceEntryId ? $"allstarr-entry-{sourceEntryId:N}" :
                    $"{playlist.ProtocolId}-entry-{track.SourcePosition}");
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
        var originals = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var track in tracks.Where(track => track.RouteKind == TrackRouteKind.Local && track.NativeEntryJson != null))
            if (JsonNode.Parse(track.NativeEntryJson!) is JsonObject item)
                originals.TryAdd(track.BackendItemId, item);
        var ids = tracks
            .Where(track => track.RouteKind == TrackRouteKind.Local && !originals.ContainsKey(track.BackendItemId))
            .Select(track => track.BackendItemId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0 || proxyService == null) return originals;

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

        foreach (var item in batches.SelectMany(batch => batch).Where(item => item["Id"] is JsonValue))
            originals[item["Id"]!.GetValue<string>()] = item;
        return originals;
    }

    private static void AddSourceLabels(JsonObject item, VirtualPlaylistTrack track, string? provider, ListeningPreferences? preferences)
    {
        item["Name"] = ExternalTrackPresentation.Title(track.Title, track.SourceMetadata?.IsExplicit == true, preferences);
        foreach (var name in new[] { "Album", "AlbumArtist" })
            Label(item, name, preferences);
        if (item["Artists"] is JsonArray artists)
            for (var index = 0; index < artists.Count; index++)
                if (artists[index] is JsonValue value && value.TryGetValue<string>(out var artist))
                    artists[index] = ExternalTrackPresentation.Name(artist, preferences);
        foreach (var collectionName in new[] { "ArtistItems", "AlbumArtists" })
            if (item[collectionName] is JsonArray values)
                foreach (var value in values.OfType<JsonObject>())
                    Label(value, "Name", preferences);

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

    private static void Label(JsonObject item, string name, ListeningPreferences? preferences)
    {
        if (item[name] is JsonValue value && value.TryGetValue<string>(out var text))
            item[name] = ExternalTrackPresentation.Name(text, preferences);
    }

    private Dictionary<string, object?> FallbackItem(VirtualPlaylistTrack track, IQueryCollection? query)
    {
        var item = _builder.ConvertSongToJellyfinItem(new Song
        {
            Id = track.BackendItemId,
            Title = track.Title,
            Artist = track.Artist,
            Artists = track.SourceMetadata?.Artists is { Count: > 0 } artists ? artists.ToList() : [track.Artist],
            Album = track.Album ?? string.Empty,
            AlbumArtist = track.AlbumArtist,
            Track = track.SourcePosition + 1,
            IsLocal = true
        }, null, query);
        item["RunTimeTicks"] = track.DurationMilliseconds * TimeSpan.TicksPerMillisecond;
        item["UserData"] = UserData(track.BackendItemId);
        item["ImageTags"] = track.CoverArtReference == null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Primary"] = track.CoverArtReference };
        return item;
    }

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
