using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private sealed record ExternalArtistQueryData(List<Song> Tracks, List<Album> Albums, Artist? Artist);
    private readonly Dictionary<string, ExternalArtistQueryData> _artistQueryData = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<Dictionary<string, object?>>> _curatorQueryRows = new(StringComparer.OrdinalIgnoreCase);

    private async Task<List<Dictionary<string, object?>>> ReadCuratorQueryRowsAsync(string externalId)
    {
        var name = externalId.Replace("curator-", "", StringComparison.OrdinalIgnoreCase);
        if (_curatorQueryRows.TryGetValue(name, out var cached)) return cached;
        var playlists = await _providerGateway.SearchPlaylistsAsync(HttpContext.RequireProtocolExecutionContext(), name, 50);
        var rows = playlists.Where(playlist => string.Equals(playlist.CuratorName, name, StringComparison.OrdinalIgnoreCase))
            .Select(_responseBuilder.ConvertPlaylistToAlbumItem).ToList();
        _curatorQueryRows[name] = rows;
        return rows;
    }

    private readonly Dictionary<string, Guid> _queryRecordings = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, object?> QuerySong(Song song)
    {
        if (song.CanonicalRecordingId is { } recording && recording != Guid.Empty) _queryRecordings[song.Id] = recording;
        return _responseBuilder.ConvertSongToJellyfinItem(song,
            ExternalTrackPresentation.Preferences(HttpContext), null, includeAllFields: true);
    }

    private async Task<IActionResult> ReadArtistSearchAsync(string cleanQuery, int limit)
    {
        var nativeTask = GetLocalArtistsResultForCurrentRequest(cleanQuery);
        var providerTask = _providerGateway.SearchAsync(HttpContext.RequireProtocolExecutionContext(), cleanQuery, 0, 0, 200);
        await Task.WhenAll(nativeTask, providerTask);
        var (native, _) = await nativeTask;
        using (native)
        {
            if (native != null) CaptureNativeQueryRows(native.RootElement);
            var rows = native != null && native.RootElement.TryGetProperty("Items", out var items)
                ? items.EnumerateArray().Select(item => item.Deserialize<Dictionary<string, object?>>() ?? []).ToList() : [];
            var names = rows.Select(item => JellyfinProviderQuery.Text(item, "Name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows.AddRange((await providerTask).Artists.Where(artist => !names.Contains(artist.Name))
                .Select(_responseBuilder.ConvertArtistForQuery));
            return await ShapeProviderQueryAsync(rows, "SortName", nativeAlreadyFiltered: true, defaultLimit: limit);
        }
    }

    private async Task<IActionResult> ShapeProviderQueryAsync(IEnumerable<Dictionary<string, object?>> rows,
        string? defaultSort = null, bool deduplicate = true, bool nativeAlreadyFiltered = false, int defaultLimit = int.MaxValue)
    {
        var items = rows.ToList();
        var context = HttpContext.RequireProtocolExecutionContext();
        var requestedUser = Request.RouteValues.TryGetValue("userId", out var routeUser) ? routeUser?.ToString() : Request.Query["UserId"].ToString();
        if (_favoriteViews != null && items.Any(IsProviderQueryItem) &&
            (string.IsNullOrWhiteSpace(requestedUser) || requestedUser == context.VerifiedBackendPrincipalId))
        {
            var favorites = await _favoriteViews.ReadAsync(context, HttpContext.RequestAborted);
            var ids = favorites.Select(item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Where(IsProviderQueryItem))
                if (ids.Contains(JellyfinProviderQuery.Text(item, "Id")) &&
                    item.GetValueOrDefault("UserData") is IDictionary<string, object> data)
                    data["IsFavorite"] = true;
        }
        var query = new JellyfinProviderQuery(Request.Query, GetEffectiveSearchTerm(Request.Query["SearchTerm"], Request.QueryString.Value), defaultLimit);
        Func<IEnumerable<Dictionary<string, object?>>, IEnumerable<Dictionary<string, object?>>>? reconcile = deduplicate ? ReconcileQueryRows : null;
        var ordered = query.FilterAndSort(items, defaultSort, deduplicate, nativeAlreadyFiltered, reconcile);
        foreach (var item in ordered.Where(IsProviderQueryItem)) new JellyfinItemFields(Request.Query).Apply(item);
        var response = _searchProtocolAdapter.ShapeItemsResponse(ordered, query.StartIndex, query.Limit);
        return new ContentResult
        {
            Content = PreserveNativeQueryRows(response.Body),
            ContentType = response.ContentType,
            StatusCode = response.StatusCode
        };
    }

    private static bool IsProviderQueryItem(Dictionary<string, object?> item) =>
        JellyfinProviderQuery.Text(item, "Id").StartsWith("ext-", StringComparison.OrdinalIgnoreCase);

    private async Task<IActionResult?> TryReadExternalQueryAsync(string? includeItemTypes, string? userId)
    {
        string[] relations = ["ArtistIds", "AlbumArtistIds", "ContributingArtistIds", "AlbumIds"];
        var requested = relations.ToDictionary(key => key, key => JellyfinProviderQuery.Values(Request.Query[key].ToString()));
        var external = requested.ToDictionary(pair => pair.Key, pair => pair.Value
            .Where(id => _localLibraryService.ParseExternalId(id).isExternal).ToArray());
        if (!external.Values.Any(ids => ids.Length > 0)) return null;

        var rows = new List<Dictionary<string, object?>>();
        var cache = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, HashSet<string>>();
        foreach (var (relation, ids) in external)
        {
            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var parsed = _localLibraryService.ParseExternalId(id);
                var role = relation == "AlbumArtistIds" ? ExternalArtistRelation.AlbumArtist :
                    relation == "ContributingArtistIds" ? ExternalArtistRelation.ContributingArtist : ExternalArtistRelation.All;
                var key = $"{id}:{role}";
                if (!cache.TryGetValue(key, out var children))
                {
                    children = await ReadExternalChildRowsAsync(parsed.provider!, relation == "AlbumIds" ? "album" : "artist", parsed.externalId!,
                        includeItemTypes, HttpContext.RequestAborted, id, role) ?? [];
                    cache[key] = children;
                }
                rows.AddRange(children);
                members.UnionWith(children.SelectMany(QueryIdentities));
            }
            if (requested[relation].Length > 0) groups[relation] = members;
        }

        var nativeIds = requested.ToDictionary(pair => pair.Key, pair => pair.Value
            .Where(id => !_localLibraryService.ParseExternalId(id).isExternal).ToArray());
        if (nativeIds.Values.Any(ids => ids.Length > 0))
        {
            var query = Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            foreach (var relation in relations)
            {
                query.Remove(relation);
                if (nativeIds[relation].Length > 0) query[relation] = string.Join(',', nativeIds[relation]);
            }
            query["StartIndex"] = "0";
            query["Limit"] = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var endpoint = userId == null ? "Items" : $"Users/{Uri.EscapeDataString(userId)}/Items";
            var (native, status) = await _proxyService.GetJsonAsync(endpoint, query, Request.Headers);
            using (native)
            {
                if (status == 200 && native != null && native.RootElement.TryGetProperty("Items", out var items))
                {
                    CaptureNativeQueryRows(native.RootElement);
                    rows.InsertRange(0, items.EnumerateArray().Select(item => item.Deserialize<Dictionary<string, object?>>() ?? []));
                }
            }
        }
        var selected = rows.Where(item => groups.All(group =>
            QueryIdentities(item).Any(group.Value.Contains) ||
            nativeIds[group.Key].Length > 0 && (!IsProviderQueryItem(item) || MatchesNativeRelation(item, group.Key, nativeIds[group.Key])))).ToList();
        return await ShapeProviderQueryAsync(selected, "ParentIndexNumber,IndexNumber,SortName", nativeAlreadyFiltered: true);
    }

    private static bool MatchesNativeRelation(Dictionary<string, object?> item, string relation, string[] ids)
    {
        var root = JsonSerializer.SerializeToElement(item);
        bool Contains(string key) => root.TryGetProperty(key, out var artists) && artists.ValueKind == JsonValueKind.Array &&
            artists.EnumerateArray().Any(artist => artist.TryGetProperty("Id", out var id) && ids.Contains(id.GetString(), StringComparer.OrdinalIgnoreCase));
        return relation switch
        {
            "AlbumIds" => ids.Contains(JellyfinProviderQuery.Text(item, "AlbumId"), StringComparer.OrdinalIgnoreCase),
            "AlbumArtistIds" => Contains("AlbumArtists"),
            "ContributingArtistIds" => Contains("ArtistItems") && !Contains("AlbumArtists"),
            _ => Contains("ArtistItems") || Contains("AlbumArtists")
        };
    }

    private IEnumerable<Dictionary<string, object?>> ReconcileQueryRows(IEnumerable<Dictionary<string, object?>> rows)
    {
        var candidates = rows.ToList();
        var native = candidates.Where(item => !IsProviderQueryItem(item)).ToArray();
        var identities = native.SelectMany(QueryIdentities).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in native) yield return item;
        foreach (var item in candidates.Where(IsProviderQueryItem))
        {
            var keys = QueryIdentities(item).ToArray();
            if (keys.Any(identities.Contains)) continue;
            identities.UnionWith(keys);
            yield return item;
        }
    }

    private IEnumerable<string> QueryIdentities(Dictionary<string, object?> item)
    {
        var id = JellyfinProviderQuery.Text(item, "Id");
        yield return "id:" + id;
        if (_queryRecordings.TryGetValue(id, out var recording)) yield return $"recording:{recording}";
        if (!item.TryGetValue("ProviderIds", out var providerIds)) yield break;
        var ids = JsonSerializer.SerializeToElement(providerIds);
        if (ids.ValueKind != JsonValueKind.Object) yield break;
        foreach (var pair in ids.EnumerateObject())
            if (!string.IsNullOrWhiteSpace(pair.Value.ToString())) yield return $"{JellyfinProviderQuery.Text(item, "Type")}:{pair.Name}:{pair.Value}";
    }

    private async Task<List<Dictionary<string, object?>>?> ReadExternalChildRowsAsync(string provider, string type,
        string externalId, string? includeItemTypes, CancellationToken cancellationToken,
        string? selectedArtistId = null, ExternalArtistRelation artistRelation = ExternalArtistRelation.All)
    {
        if (type == "artist" && externalId.StartsWith("curator-", StringComparison.OrdinalIgnoreCase))
            return artistRelation == ExternalArtistRelation.ContributingArtist ? [] : await ReadCuratorQueryRowsAsync(externalId);
        var types = ParseItemTypes(includeItemTypes);
        var unspecified = types == null || types.Length == 0;
        var includeTracks = types?.Contains("Audio", StringComparer.OrdinalIgnoreCase) == true;
        if (type == "album")
        {
            if (!unspecified && !includeTracks) return [];
            var album = await GetProviderAlbumAsync(provider, externalId, cancellationToken);
            if (album == null) return null;
            var excludedArtists = JellyfinProviderQuery.Values(Request.Query["ExcludeArtistIds"].ToString());
            if (album.ArtistId != null && excludedArtists.Contains(album.ArtistId, StringComparer.OrdinalIgnoreCase)) return [];
            return album.Songs.Select(QuerySong).ToList();
        }
        if (type != "artist") return [];
        var includeAlbums = unspecified || types!.Contains("MusicAlbum", StringComparer.OrdinalIgnoreCase);
        if (!includeTracks && !includeAlbums) return [];
        selectedArtistId ??= $"ext-{provider}-artist-{externalId}";
        var key = $"{provider}:{externalId}";
        if (!_artistQueryData.TryGetValue(key, out var data))
        {
            var contributes = artistRelation == ExternalArtistRelation.ContributingArtist ||
                JellyfinProviderQuery.Values(Request.Query["ContributingArtistIds"].ToString())
                    .Contains(selectedArtistId, StringComparer.OrdinalIgnoreCase);
            var tracksTask = includeTracks || contributes
                ? GetProviderArtistTracksAsync(provider, externalId, cancellationToken) : Task.FromResult(new List<Song>());
            var albumsTask = GetProviderArtistAlbumsAsync(provider, externalId, cancellationToken);
            var artistTask = includeAlbums ? GetProviderArtistAsync(provider, externalId, cancellationToken) : Task.FromResult<Artist?>(null);
            await Task.WhenAll(tracksTask, albumsTask, artistTask);
            var allTracks = await tracksTask;
            var allAlbums = (await albumsTask).ToList();
            if (contributes)
            {
                var known = allAlbums.Select(album => album.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var lookups = allTracks.Select(track => track.AlbumId)
                    .Where(id => !string.IsNullOrWhiteSpace(id) && !known.Contains(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id => _localLibraryService.ParseExternalId(id!))
                    .Where(parsed => parsed.isExternal && parsed.type == "album" &&
                        string.Equals(parsed.provider, provider, StringComparison.OrdinalIgnoreCase))
                    .Take(200)
                    .Select(parsed => GetProviderAlbumAsync(provider, parsed.externalId!, cancellationToken));
                allAlbums.AddRange((await Task.WhenAll(lookups)).OfType<Album>());
            }
            data = new(allTracks, allAlbums, await artistTask);
            _artistQueryData[key] = data;
        }
        var tracks = data.Tracks.ToList();
        var albums = data.Albums.ToList();
        var artist = data.Artist;
        bool IsPrimaryAlbum(Album album) => !string.IsNullOrWhiteSpace(album.ArtistId)
            ? string.Equals(album.ArtistId, selectedArtistId, StringComparison.OrdinalIgnoreCase)
            : artist != null && string.Equals(album.Artist, artist.Name, StringComparison.OrdinalIgnoreCase);
        if (includeTracks)
        {
            tracks.AddRange(albums.SelectMany(album => album.Songs.Where(track => IsPrimaryAlbum(album) ||
                string.Equals(track.ArtistId, selectedArtistId, StringComparison.OrdinalIgnoreCase) ||
                track.ArtistIds.Contains(selectedArtistId, StringComparer.OrdinalIgnoreCase))));
            var excludedArtists = JellyfinProviderQuery.Values(Request.Query["ExcludeArtistIds"].ToString());
            var excludedAlbums = albums.Where(album => album.ArtistId != null && excludedArtists.Contains(album.ArtistId, StringComparer.OrdinalIgnoreCase))
                .Select(album => album.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            tracks = tracks.Where(track => track.AlbumId == null || !excludedAlbums.Contains(track.AlbumId)).ToList();
        }
        if (artistRelation != ExternalArtistRelation.All)
        {
            albums = albums.Where(album => (!string.IsNullOrWhiteSpace(album.ArtistId) || artist != null && !string.IsNullOrWhiteSpace(album.Artist)) &&
                IsPrimaryAlbum(album) == (artistRelation == ExternalArtistRelation.AlbumArtist)).ToList();
            var albumIds = albums.Select(album => album.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            tracks = tracks.Where(track => track.AlbumId != null && albumIds.Contains(track.AlbumId)).ToList();
        }
        var result = new List<Dictionary<string, object?>>();
        foreach (var album in includeAlbums ? albums : [])
        {
            if (!album.IsLocal && artist != null && artistRelation != ExternalArtistRelation.ContributingArtist)
            {
                if (string.IsNullOrEmpty(album.Artist)) album.Artist = artist.Name;
                if (string.IsNullOrEmpty(album.ArtistId)) album.ArtistId = artist.Id;
            }
            var item = _responseBuilder.ConvertAlbumForQuery(album);
            result.Add(item);
        }
        result.AddRange((includeTracks ? tracks : []).Select(QuerySong));
        return result;
    }
}
