using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Globalization;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Search

    [HttpGet("Items", Order = 1)]
    [HttpGet("Users/{userId}/Items", Order = 1)]
    public async Task<IActionResult> SearchItems(
        [FromQuery] string? searchTerm,
        [FromQuery] string? includeItemTypes,
        [FromQuery] int limit = 20,
        [FromQuery] int startIndex = 0,
        [FromQuery] string? parentId = null,
        [FromQuery] string? artistIds = null,
        [FromQuery] string? albumArtistIds = null,
        [FromQuery] string? contributingArtistIds = null,
        [FromQuery] string? albumIds = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool recursive = true,
        string? userId = null)
    {
        if (IsFavoritesOnlyRequest())
            return await ReadFavoriteItemsAsync(GetEffectiveSearchTerm(searchTerm, Request.QueryString.Value),
                includeItemTypes, startIndex, limit, userId);

        if (Request.Query.TryGetValue("Ids", out var requestedIds))
            return await ReadItemsByIdsAsync(requestedIds.ToString(), startIndex,
                Request.Query.ContainsKey("Limit") ? limit : int.MaxValue, userId);

        var boundSearchTerm = searchTerm;
        searchTerm = GetEffectiveSearchTerm(searchTerm, Request.QueryString.Value);

        // Preserve Jellyfin's distinct primary and contributing artist relationships.
        var effectiveArtistIds = albumArtistIds ?? contributingArtistIds ?? artistIds;
        var favoritesOnlyRequest = IsFavoritesOnlyRequest();

        _logger.LogDebug("=== SEARCHITEMS V2 CALLED === searchTerm={SearchTerm}, includeItemTypes={ItemTypes}, parentId={ParentId}, artistIds={ArtistIds}, albumArtistIds={AlbumArtistIds}, contributingArtistIds={ContributingArtistIds}, albumIds={AlbumIds}, userId={UserId}",
            searchTerm, includeItemTypes, parentId, artistIds, albumArtistIds, contributingArtistIds, albumIds, userId);
        _logger.LogInformation(
            "SEARCH TRACE: rawQuery='{RawQuery}', boundSearchTerm='{BoundSearchTerm}', effectiveSearchTerm='{EffectiveSearchTerm}', includeItemTypes='{IncludeItemTypes}'",
            Request.QueryString.Value ?? string.Empty,
            boundSearchTerm ?? string.Empty,
            searchTerm ?? string.Empty,
            includeItemTypes ?? string.Empty);

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            var nativeLibraryRoot = await TryReadNativeMusicLibraryRootAsync(
                searchTerm, includeItemTypes, parentId, effectiveArtistIds, albumIds, startIndex, limit);
            if (nativeLibraryRoot != null) return nativeLibraryRoot;

            var savedBrowse = await TryReadSavedBrowseAsync(includeItemTypes);
            if (savedBrowse != null) return savedBrowse;
        }

        var externalQuery = await TryReadExternalQueryAsync(includeItemTypes, userId);
        if (externalQuery != null) return externalQuery;

        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var durable = await _virtualPlaylistProtocolAdapter.ReadItemsAsync(
                HttpContext.RequireProtocolExecutionContext(), parentId,
                Request.Headers, Request.Query, HttpContext.RequestAborted);
            if (durable != null) return durable;

            if (_spotifySettings.Enabled && _spotifySettings.IsSpotifyPlaylist(parentId))
            {
                return await GetPlaylistTracks(parentId);
            }

            if (PlaylistIdHelper.IsExternalPlaylist(parentId))
            {
                return await GetPlaylistTracks(parentId);
            }

            var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(parentId);

            if (isExternal)
            {
                _logger.LogDebug("Fetching children for external parent: {Provider}/{Type}/{ExternalId}",
                    provider, type, externalId);
                return await GetExternalChildItems(provider!, type!, externalId!, includeItemTypes, HttpContext.RequestAborted);
            }

            var isMusicLibrary = parentId == _settings.LibraryId;

            if (isMusicLibrary && !string.IsNullOrWhiteSpace(searchTerm))
            {
                _logger.LogDebug("Searching within music library {ParentId}, including external sources",
                    parentId);
            }
            else
            {
                _logger.LogDebug("Library ParentId detected, proxying entire request to Jellyfin");
            }
        }

        if (favoritesOnlyRequest)
            return await ReadFavoriteItemsAsync(searchTerm, includeItemTypes, startIndex, limit, userId);

        if (string.IsNullOrWhiteSpace(searchTerm) && !favoritesOnlyRequest)
        {
            var catalog = await TryReadNativeCatalogAsync(effectiveArtistIds, parentId, includeItemTypes, userId);
            if (catalog != null) return catalog;
        }

        if (!string.IsNullOrWhiteSpace(effectiveArtistIds))
        {
            _logger.LogDebug("Library artist filter requested, proxying to Jellyfin");
            var endpoint = userId != null
                ? $"Users/{userId}/Items{Request.QueryString}"
                : $"Items{Request.QueryString}";
            var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
            return HandleProxyResponse(result, statusCode);
        }

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            _logger.LogDebug("Browse request with no filters, proxying to Jellyfin with full query string");

            var endpoint = userId != null ? $"Users/{userId}/Items" : "Items";

            // Include MediaSources only for audio-oriented browse requests (bitrate needs).
            // Album/artist browse requests should stay as close to raw Jellyfin responses as possible.
            var queryString = Request.QueryString.Value ?? "";
            var browseQueryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
            if (!browseQueryParams.TryGetValue("IncludeItemTypes", out var browseTypes) ||
                string.IsNullOrWhiteSpace(browseTypes.ToString()))
            {
                var constrained = browseQueryParams.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                constrained["IncludeItemTypes"] = JellyfinMusicEndpointPolicy.DefaultMusicItemTypes;
                queryString = "?" + string.Join("&", constrained.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
                _logger.LogDebug("Constrained untyped browse request to music item types");
            }
            var requestedTypes = ParseItemTypes(includeItemTypes);
            var shouldIncludeMediaSources = requestedTypes != null &&
                                            requestedTypes.Contains("Audio", StringComparer.OrdinalIgnoreCase);

            if (shouldIncludeMediaSources && !string.IsNullOrEmpty(queryString))
            {
                var queryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);

                if (queryParams.ContainsKey("Fields"))
                {
                    var fieldsValue = queryParams["Fields"].ToString();
                    if (!fieldsValue.Contains("MediaSources", StringComparison.OrdinalIgnoreCase))
                    {
                        var newFields = string.IsNullOrEmpty(fieldsValue)
                            ? "MediaSources"
                            : $"{fieldsValue},MediaSources";

                        var newQueryParams = new Dictionary<string, string>();
                        foreach (var kvp in queryParams)
                        {
                            if (kvp.Key == "Fields")
                            {
                                newQueryParams[kvp.Key] = newFields;
                            }
                            else
                            {
                                newQueryParams[kvp.Key] = kvp.Value.ToString();
                            }
                        }

                        queryString = "?" + string.Join("&", newQueryParams.Select(kvp =>
                            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
                    }
                }
                else
                {
                    queryString = $"{queryString}&Fields=MediaSources";
                }
            }
            else if (shouldIncludeMediaSources)
            {
                queryString = "?Fields=MediaSources";
            }

            if (!string.IsNullOrEmpty(queryString))
            {
                endpoint = $"{endpoint}{queryString}";
            }

            var (browseResult, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

            // Preserve upstream error bodies and status codes.
            if (browseResult == null)
            {
                _logger.LogDebug("Jellyfin returned {StatusCode}, passing through to client", statusCode);
                return HandleProxyResponse(browseResult, statusCode);
            }

            if (statusCode == 200) await RememberNativeCatalogTypesAsync(browseResult.RootElement);

            if (ShouldProcessSpotifyPlaylistCounts(browseResult, includeItemTypes))
            {
                _logger.LogDebug("Browse result has Items, checking for Spotify playlists to update counts");
            }

            if (requestedTypes?.Contains("Playlist", StringComparer.OrdinalIgnoreCase) == true)
            {
                return await MergeVirtualPlaylistBrowseAsync(
                    browseResult, limit, startIndex, HttpContext.RequestAborted);
            }

            var result = JsonSerializer.Deserialize<object>(browseResult.RootElement.GetRawText());
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var rawText = browseResult.RootElement.GetRawText();
                var preview = rawText.Length > 200 ? rawText[..200] : rawText;
                _logger.LogDebug("Jellyfin browse result preview: {Result}", preview);
            }

            if (_configuration.GetValue<bool>("Debug:LogAllRequests") &&
                browseResult.RootElement.TryGetProperty("Items", out var tracedItems) &&
                tracedItems.ValueKind == JsonValueKind.Array)
            {
                var itemCount = tracedItems.GetArrayLength();
                var primaryImageCount = tracedItems.EnumerateArray().Count(item =>
                    item.TryGetProperty("ImageTags", out var imageTags) &&
                    imageTags.ValueKind == JsonValueKind.Object &&
                    imageTags.TryGetProperty("Primary", out var primaryTag) &&
                    !string.IsNullOrWhiteSpace(primaryTag.GetString()));
                _logger.LogInformation(
                    "BROWSE RESPONSE TRACE: path={RequestPath}, items={ItemCount}, primary images advertised={PrimaryImageCount}",
                    Request.Path.Value,
                    itemCount,
                    primaryImageCount);
            }

            return new JsonResult(result);
        }

        var cleanQuery = searchTerm?.Trim().Trim('"') ?? "";
        _logger.LogDebug("Performing integrated search for: {Query}", cleanQuery);

        var itemTypes = ParseItemTypes(includeItemTypes);
        var artistSearch = itemTypes?.Length == 1 && itemTypes[0].Equals("MusicArtist", StringComparison.OrdinalIgnoreCase);
        var integratedFetchLimit = artistSearch ? 200 : GetIntegratedSearchFetchLimit(startIndex, limit);
        var externalSearchLimits = GetExternalSearchLimits(itemTypes, integratedFetchLimit, includePlaylistsAsAlbums: true);
        _logger.LogInformation(
            "SEARCH TRACE: external limits for query '{Query}' => songs={SongLimit}, albums={AlbumLimit}, artists={ArtistLimit}",
            cleanQuery,
            externalSearchLimits.SongLimit,
            externalSearchLimits.AlbumLimit,
            externalSearchLimits.ArtistLimit);

        var context = HttpContext.RequireProtocolExecutionContext();
        var merged = await _musicSearch.SearchAsync(
            context,
            new MusicSearchRequest(
                cleanQuery,
                new MusicSearchWindow(externalSearchLimits.SongLimit),
                new MusicSearchWindow(externalSearchLimits.AlbumLimit),
                new MusicSearchWindow(externalSearchLimits.ArtistLimit),
                _settings.EnableExternalPlaylists && !artistSearch ? new MusicSearchWindow(integratedFetchLimit) : MusicSearchWindow.None,
                IncludeProviders: !favoritesOnlyRequest,
                HideProviderArtistsNamedLikeNative: true),
            new JellyfinNativeMusicSearch(
                cancellationToken => GetLocalSearchResultForCurrentRequest(
                    cleanQuery, includeItemTypes, artistSearch ? int.MaxValue : integratedFetchLimit, recursive, userId),
                (id, cancellationToken) => _proxyService.GetJsonAsync(
                    $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(id)}",
                    null, Request.Headers), CaptureNativeQueryRows),
            HttpContext.RequestAborted);

        // Native rows stay Jellyfin-shaped; only provider rows are synthesized.
        var allSongs = merged.Songs.Select(RenderSearchEntry).ToList();
        var allArtists = merged.Artists.Select(entry => artistSearch ? RenderQuerySearchEntry(entry) : RenderSearchEntry(entry)).ToList();
        var mergedAlbumsAndPlaylists = merged.Albums.Select(RenderSearchEntry).ToList();

        _logger.LogDebug(
            "Merged results: Songs={Songs}, Albums+Playlists={AlbumsPlaylists}, Artists={Artists}",
            allSongs.Count, mergedAlbumsAndPlaylists.Count, allArtists.Count);

        var items = new List<Dictionary<string, object?>>();

        _logger.LogDebug("Filtering by item types: {ItemTypes}",
            itemTypes == null ? "null" : string.Join(",", itemTypes));

        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("MusicArtist"))
        {
            _logger.LogDebug("Adding {Count} artists to results", allArtists.Count);
            items.AddRange(allArtists);
        }

        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("MusicAlbum") ||
            itemTypes.Contains("Playlist"))
        {
            _logger.LogDebug("Adding {Count} albums+playlists to results", mergedAlbumsAndPlaylists.Count);
            items.AddRange(mergedAlbumsAndPlaylists);
        }

        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("Audio"))
        {
            _logger.LogDebug("Adding {Count} songs to results", allSongs.Count);
            items.AddRange(allSongs);
        }

        if (artistSearch) return await ShapeProviderQueryAsync(items, nativeAlreadyFiltered: true, defaultLimit: limit);
        var response = _searchProtocolAdapter.ShapeItemsResponse(items, startIndex, limit);
        return new ContentResult { Content = response.Body, ContentType = response.ContentType, StatusCode = response.StatusCode };
    }

    private Dictionary<string, object?> RenderQuerySearchEntry(MusicSearchEntry<Dictionary<string, object?>> entry)
    {
        if (entry.Native != null) return entry.Native.Payload;
        return _responseBuilder.ConvertArtistForQuery(entry.Artist!);
    }

    private Dictionary<string, object?> RenderSearchEntry(MusicSearchEntry<Dictionary<string, object?>> entry)
    {
        if (entry.Native != null) return entry.Native.Payload;
        if (entry.Song != null) return _responseBuilder.ConvertSongToJellyfinItem(entry.Song);
        if (entry.Album != null) return _responseBuilder.ConvertAlbumToJellyfinItem(entry.Album);
        // Jellyfin search cannot mix playlists directly, so they are shown as albums.
        if (entry.Playlist != null) return _responseBuilder.ConvertPlaylistToAlbumItem(entry.Playlist);
        return _responseBuilder.ConvertArtistToJellyfinItem(entry.Artist!);
    }

    internal static NativeMusicItem<Dictionary<string, object?>>? ToNativeSearchItem(Dictionary<string, object?> item)
    {
        MusicItemKind? kind = GetItemType(item) switch
        {
            "Audio" => MusicItemKind.Song,
            "MusicAlbum" => MusicItemKind.Album,
            "MusicArtist" => MusicItemKind.Artist,
            _ => null
        };
        return kind == null
            ? null
            : new(kind.Value, GetItemStringValue(item, "Id"), GetItemName(item), SearchArtistText(item), item)
            {
                AlbumText = GetItemStringValue(item, "Album"),
                AlbumArtistText = GetItemStringValue(item, "AlbumArtist"),
                AlbumMetadata = kind == MusicItemKind.Album ? NativeAlbumMetadata(item) : null,
                DurationSeconds = long.TryParse(GetItemStringValue(item, "RunTimeTicks"), out var ticks)
                    ? (double)ticks / TimeSpan.TicksPerSecond : null
            };
    }

    private static Album NativeAlbumMetadata(Dictionary<string, object?> item)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetValue("ProviderIds", out var raw))
        {
            if (raw is JsonElement { ValueKind: JsonValueKind.Object } json)
            {
                foreach (var property in json.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String) ids[property.Name] = property.Value.GetString()!;
            }
            else if (raw is IDictionary<string, string> values)
            {
                foreach (var pair in values) ids[pair.Key] = pair.Value;
            }
        }
        return new()
        {
            Id = GetItemStringValue(item, "Id"),
            Title = GetItemName(item),
            Artist = SearchArtistText(item) ?? string.Empty,
            IsLocal = true,
            ProviderIds = ids,
            Upc = ids.GetValueOrDefault("UPC") ?? GetItemStringValue(item, "UPC"),
            IsExplicit = GetItemStringValue(item, "OfficialRating").ToLowerInvariant() switch
            { "explicit" => true, "clean" or "cleaned" => false, _ => null },
            SongCount = int.TryParse(GetItemStringValue(item, "ChildCount"), out var count) && count > 0 ? count : null,
            Edition = GetItemStringValue(item, "Edition")
        };
    }

    /// <summary>The backend half of a Jellyfin integrated search.</summary>
    private sealed class JellyfinNativeMusicSearch(
        Func<CancellationToken, Task<(JsonDocument? Body, int StatusCode)>> search,
        Func<string, CancellationToken, Task<(JsonDocument? Body, int StatusCode)>> getItem,
        Action<JsonElement>? capture = null)
        : INativeMusicSearch<Dictionary<string, object?>>
    {
        public async Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> SearchAsync(
            CancellationToken cancellationToken)
        {
            var (document, _) = await search(cancellationToken);
            using (document)
            {
                if (document == null || !document.RootElement.TryGetProperty("Items", out var items)) return [];
                capture?.Invoke(document.RootElement);
                return items.EnumerateArray()
                    .Where(item => item.TryGetProperty("Type", out _))
                    .Select(item => ToNativeSearchItem(item.Deserialize<Dictionary<string, object?>>() ?? []))
                    .OfType<NativeMusicItem<Dictionary<string, object?>>>()
                    .ToList();
            }
        }

        public async Task<NativeMusicItem<Dictionary<string, object?>>?> GetSongAsync(
            string id, CancellationToken cancellationToken)
        {
            var (document, status) = await getItem(id, cancellationToken);
            using (document)
            {
                if (status != StatusCodes.Status200OK || document == null ||
                    !document.RootElement.TryGetProperty("Id", out var itemId) || itemId.GetString() != id ||
                    !document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "Audio") return null;
                capture?.Invoke(document.RootElement);
                var item = document.RootElement.Deserialize<Dictionary<string, object?>>();
                return item == null ? null : ToNativeSearchItem(item);
            }
        }
    }

    private async Task<(JsonDocument? Body, int StatusCode)> GetLocalSearchResultForCurrentRequest(
        string cleanQuery,
        string? includeItemTypes,
        int fetchLimit,
        bool recursive,
        string? userId)
    {
        var endpoint = !string.IsNullOrWhiteSpace(userId)
            ? $"Users/{userId}/Items"
            : "Items";

        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in Request.Query)
        {
            queryParams[kvp.Key] = kvp.Value.ToString();
        }

        // Normalize recovered SearchTerm while preserving every other client parameter.
        queryParams["SearchTerm"] = cleanQuery;
        // Each source contributes the same prefix; apply client paging only after merging.
        queryParams["StartIndex"] = "0";
        queryParams["Limit"] = fetchLimit.ToString(CultureInfo.InvariantCulture);

        _logger.LogInformation(
            "SEARCH TRACE: local proxy request endpoint='{Endpoint}' query='{SafeQuery}'",
            endpoint,
            ToSafeQueryStringForLogs(queryParams));

        return await _proxyService.GetJsonAsync(endpoint, queryParams, Request.Headers);
    }

    [HttpGet("Search/Hints", Order = 1)]
    [HttpGet("Users/{userId}/Search/Hints", Order = 1)]
    public async Task<IActionResult> SearchHints(
        [FromQuery] string searchTerm,
        [FromQuery] int limit = 20,
        [FromQuery] string? includeItemTypes = null,
        string? userId = null)
    {
        searchTerm = GetEffectiveSearchTerm(searchTerm, Request.QueryString.Value) ?? searchTerm;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                SearchHints = Array.Empty<object>(),
                TotalRecordCount = 0
            });
        }

        var cleanQuery = searchTerm.Trim().Trim('"');
        var requestedTypes = ParseItemTypes(includeItemTypes);
        var externalSearchLimits = GetExternalSearchLimits(requestedTypes, limit, includePlaylistsAsAlbums: false);

        _logger.LogInformation(
            "SEARCH TRACE: hint limits for query '{Query}' => songs={SongLimit}, albums={AlbumLimit}, artists={ArtistLimit}",
            cleanQuery,
            externalSearchLimits.SongLimit,
            externalSearchLimits.AlbumLimit,
            externalSearchLimits.ArtistLimit);

        var context = HttpContext.RequireProtocolExecutionContext();
        var native = new JellyfinNativeHintSearch(
            cancellationToken => GetLocalSearchHintsResultForCurrentRequest(cleanQuery, userId),
            (id, cancellationToken) => _proxyService.GetJsonAsync(
                $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(id)}",
                null, Request.Headers));
        // A kind the client did not ask for has a zero limit, so none of its provider rows join.
        var merged = await _musicSearch.SearchAsync(
            context,
            new MusicSearchRequest(
                cleanQuery,
                new MusicSearchWindow(externalSearchLimits.SongLimit, Take: externalSearchLimits.SongLimit),
                new MusicSearchWindow(externalSearchLimits.AlbumLimit, Take: externalSearchLimits.AlbumLimit),
                new MusicSearchWindow(externalSearchLimits.ArtistLimit, Take: externalSearchLimits.ArtistLimit),
                MusicSearchWindow.None,
                HideProviderArtistsNamedLikeNative: true),
            native,
            HttpContext.RequestAborted);

        var hints = MusicSearchService.RankTogether(cleanQuery, merged).Select(RenderSearchHint)
            .Concat(native.Unranked)
            .Take(Math.Max(0, limit))
            .ToList();

        return _responseBuilder.CreateJsonResponse(new
        {
            SearchHints = hints,
            TotalRecordCount = hints.Count
        });
    }

    private Dictionary<string, object?> RenderSearchHint(MusicSearchEntry<Dictionary<string, object?>> entry)
    {
        if (entry.Native != null) return entry.Native.Payload;
        if (entry.Song != null) return _responseBuilder.ConvertSongToSearchHint(entry.Song);
        if (entry.Album != null) return _responseBuilder.ConvertAlbumToSearchHint(entry.Album);
        return _responseBuilder.ConvertArtistToSearchHint(entry.Artist!);
    }

    /// <summary>A native SearchHint with every field kept and Id and ItemId both set.</summary>
    internal static Dictionary<string, object?>? ToNativeSearchHint(JsonElement hint)
    {
        if (hint.ValueKind != JsonValueKind.Object) return null;
        var id = StringProperty(hint, "Id") ?? StringProperty(hint, "ItemId");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var preserved = hint.Deserialize<Dictionary<string, object?>>() ?? [];
        preserved["Id"] = id;
        preserved["ItemId"] = id;
        return preserved;

        static string? StringProperty(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static readonly string[] SearchHintItemFields =
    [
        "Id", "Name", "Type", "MediaType", "IsFolder", "RunTimeTicks", "ProductionYear", "IndexNumber",
        "ParentIndexNumber", "Album", "AlbumId", "AlbumArtist", "Artists", "PrimaryImageAspectRatio",
        "ChannelId", "ChannelName"
    ];

    /// <summary>Projects a full Jellyfin item onto the fields a native SearchHint carries.</summary>
    internal static Dictionary<string, object?> ToSearchHintFromItem(Dictionary<string, object?> item)
    {
        var hint = SearchHintItemFields
            .Where(item.ContainsKey)
            .ToDictionary(key => key, key => item[key]);
        hint["ItemId"] = GetItemStringValue(item, "Id");
        if (item.TryGetValue("ImageTags", out var tags) &&
            tags is JsonElement { ValueKind: JsonValueKind.Object } imageTags &&
            imageTags.TryGetProperty("Primary", out var primary) &&
            primary.ValueKind == JsonValueKind.String)
        {
            hint["PrimaryImageTag"] = primary.GetString();
        }
        return hint;
    }

    /// <summary>The backend half of a Jellyfin hint search; rows stay in SearchHint shape.</summary>
    private sealed class JellyfinNativeHintSearch(
        Func<CancellationToken, Task<(JsonDocument? Body, int StatusCode)>> search,
        Func<string, CancellationToken, Task<(JsonDocument? Body, int StatusCode)>> getItem)
        : INativeMusicSearch<Dictionary<string, object?>>
    {
        // Native hints of kinds search does not rank, such as playlists and genres.
        public List<Dictionary<string, object?>> Unranked { get; } = [];

        public async Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> SearchAsync(
            CancellationToken cancellationToken)
        {
            var (document, _) = await search(cancellationToken);
            using (document)
            {
                if (document == null ||
                    document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("SearchHints", out var hints) ||
                    hints.ValueKind != JsonValueKind.Array) return [];

                var rows = new List<NativeMusicItem<Dictionary<string, object?>>>();
                foreach (var hint in hints.EnumerateArray())
                {
                    var preserved = ToNativeSearchHint(hint);
                    if (preserved == null) continue;
                    var row = ToNativeSearchItem(preserved);
                    if (row == null) Unranked.Add(preserved);
                    else rows.Add(row);
                }
                return rows;
            }
        }

        public async Task<NativeMusicItem<Dictionary<string, object?>>?> GetSongAsync(
            string id, CancellationToken cancellationToken)
        {
            var (document, status) = await getItem(id, cancellationToken);
            using (document)
            {
                if (status != StatusCodes.Status200OK || document == null ||
                    document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Id", out var itemId) || itemId.GetString() != id ||
                    !document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "Audio") return null;
                var item = document.RootElement.Deserialize<Dictionary<string, object?>>();
                return item == null ? null : ToNativeSearchItem(ToSearchHintFromItem(item));
            }
        }
    }

    private async Task<(JsonDocument? Body, int StatusCode)> GetLocalSearchHintsResultForCurrentRequest(
        string cleanQuery,
        string? userId)
    {
        // Older clients use /Users/{id}/Search/Hints; Jellyfin expects Search/Hints?UserId=.
        const string endpoint = "Search/Hints";

        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in Request.Query)
        {
            queryParams[kvp.Key] = kvp.Value.ToString();
        }

        queryParams["SearchTerm"] = cleanQuery;
        if (!string.IsNullOrWhiteSpace(userId)) queryParams["UserId"] = userId;

        _logger.LogInformation(
            "SEARCH TRACE: local hints proxy request endpoint='{Endpoint}' query='{SafeQuery}'",
            endpoint,
            ToSafeQueryStringForLogs(queryParams));

        return await _proxyService.GetJsonAsync(endpoint, queryParams, Request.Headers);
    }

    private static string ToSafeQueryStringForLogs(IReadOnlyDictionary<string, string> queryParams)
    {
        if (queryParams.Count == 0)
        {
            return string.Empty;
        }

        var query = "?" + string.Join("&", queryParams.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}"));

        return MaskSensitiveQueryString(query);
    }

    private bool IsFavoritesOnlyRequest()
    {
        return string.Equals(Request.Query["IsFavorite"].ToString(), "true", StringComparison.OrdinalIgnoreCase) ||
            Request.Query["Filters"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("IsFavorite", StringComparer.OrdinalIgnoreCase);
    }

    internal static (int SongLimit, int AlbumLimit, int ArtistLimit) GetExternalSearchLimits(
        string[]? requestedTypes,
        int limit,
        bool includePlaylistsAsAlbums)
    {
        if (limit <= 0)
        {
            return (0, 0, 0);
        }

        if (requestedTypes == null || requestedTypes.Length == 0)
        {
            return (limit, limit, limit);
        }

        var includeSongs = requestedTypes.Contains("Audio", StringComparer.OrdinalIgnoreCase);
        var includeAlbums = requestedTypes.Contains("MusicAlbum", StringComparer.OrdinalIgnoreCase) ||
                            (includePlaylistsAsAlbums &&
                             requestedTypes.Contains("Playlist", StringComparer.OrdinalIgnoreCase));
        var includeArtists = requestedTypes.Contains("MusicArtist", StringComparer.OrdinalIgnoreCase);

        return (
            includeSongs ? limit : 0,
            includeAlbums ? limit : 0,
            includeArtists ? limit : 0);
    }

    internal static int GetIntegratedSearchFetchLimit(int startIndex, int limit) =>
        Math.Min(Math.Max(20, Math.Max(0, startIndex) + Math.Max(0, limit)), 500);

    private static IActionResult CreateEmptyItemsResponse(int startIndex)
    {
        return new JsonResult(new
        {
            Items = Array.Empty<object>(),
            TotalRecordCount = 0,
            StartIndex = startIndex
        });
    }

    internal static double CalculateItemRelevanceScore(string query, Dictionary<string, object?> item) =>
        SearchRelevanceRanker.Score(query, GetItemName(item), SearchArtistText(item),
            GetItemType(item) == "Audio" ? GetItemStringValue(item, "Album") : null);

    private static string? SearchArtistText(Dictionary<string, object?> item) => GetItemType(item) switch
    {
        "Audio" => GetSongArtistText(item),
        "MusicAlbum" => GetAlbumArtistText(item),
        _ => null
    };

    private static string GetItemName(Dictionary<string, object?> item) => GetItemStringValue(item, "Name");

    private static string GetItemType(Dictionary<string, object?> item)
    {
        return GetItemStringValue(item, "Type");
    }

    private static string GetSongArtistText(Dictionary<string, object?> item)
    {
        var artists = GetItemStringList(item, "Artists").Take(3).ToList();
        if (artists.Count > 0)
        {
            return string.Join(", ", artists);
        }

        var albumArtist = GetItemStringValue(item, "AlbumArtist");
        if (!string.IsNullOrWhiteSpace(albumArtist))
        {
            return albumArtist;
        }

        return GetItemStringValue(item, "Artist");
    }

    private static string GetAlbumArtistText(Dictionary<string, object?> item)
    {
        var albumArtist = GetItemStringValue(item, "AlbumArtist");
        if (!string.IsNullOrWhiteSpace(albumArtist))
        {
            return albumArtist;
        }

        var artists = GetItemStringList(item, "Artists").Take(3).ToList();
        if (artists.Count > 0)
        {
            return string.Join(" ", artists);
        }

        return GetItemStringValue(item, "Artist");
    }

    private static string GetItemStringValue(Dictionary<string, object?> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            return string.Empty;
        }

        if (value is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? string.Empty,
                JsonValueKind.Number => el.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => string.Empty
            };
        }

        return value.ToString() ?? string.Empty;
    }

    private static IEnumerable<string> GetItemStringList(Dictionary<string, object?> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            yield break;
        }

        if (value is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var arrayItem in el.EnumerateArray())
            {
                if (arrayItem.ValueKind == JsonValueKind.String)
                {
                    var text = arrayItem.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        yield return text;
                    }
                }
                else if (arrayItem.ValueKind == JsonValueKind.Object &&
                         arrayItem.TryGetProperty("Name", out var nameEl) &&
                         nameEl.ValueKind == JsonValueKind.String)
                {
                    var text = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        yield return text;
                    }
                }
            }

            yield break;
        }

        if (value is IEnumerable<string> stringValues)
        {
            foreach (var text in stringValues)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
            }

            yield break;
        }

        if (value is IEnumerable<object?> objectValues)
        {
            foreach (var objectValue in objectValues)
            {
                var text = objectValue?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
            }
        }
    }

    private async Task<IActionResult?> TryReadNativeMusicLibraryRootAsync(
        string? searchTerm,
        string? includeItemTypes,
        string? parentId,
        string? effectiveArtistIds,
        string? albumIds,
        int startIndex,
        int limit)
    {
        if (!IsNativeMusicLibraryRootRequest(
                searchTerm, includeItemTypes, parentId, effectiveArtistIds, albumIds))
            return null;

        var endpoint = BuildCurrentEndpoint();
        var paginated = Request.Query.ContainsKey("StartIndex") || Request.Query.ContainsKey("Limit");
        if (paginated)
        {
            var query = Request.Query
                .Where(item => !item.Key.Equals("StartIndex", StringComparison.OrdinalIgnoreCase) &&
                               !item.Key.Equals("Limit", StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.Value.Select(value =>
                    new KeyValuePair<string, string?>(item.Key, value)));
            endpoint = (Request.Path.Value?.TrimStart('/') ?? string.Empty) + QueryString.Create(query);
        }

        var (body, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
        return FilterMusicLibraryViews(body, statusCode, rootFoldersOnly: true,
            startIndex: startIndex, limit: Request.Query.ContainsKey("Limit") ? limit : null);
    }

    private bool IsNativeMusicLibraryRootRequest(
        string? searchTerm,
        string? includeItemTypes,
        string? parentId,
        string? effectiveArtistIds,
        string? albumIds)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm) ||
            !string.IsNullOrWhiteSpace(includeItemTypes) ||
            !string.IsNullOrWhiteSpace(parentId) ||
            !string.IsNullOrWhiteSpace(effectiveArtistIds) ||
            !string.IsNullOrWhiteSpace(albumIds) ||
            Request.Query.ContainsKey("Ids") ||
            IsFavoritesOnlyRequest())
            return false;

        foreach (var query in Request.Query)
        {
            if (query.Key.Equals("SearchTerm", StringComparison.OrdinalIgnoreCase) ||
                query.Key.Equals("IncludeItemTypes", StringComparison.OrdinalIgnoreCase) ||
                query.Key.Equals("ParentId", StringComparison.OrdinalIgnoreCase))
            {
                if (query.Value.Any(value => !string.IsNullOrWhiteSpace(value))) return false;
                continue;
            }

            if (query.Key.Equals("Recursive", StringComparison.OrdinalIgnoreCase))
            {
                if (query.Value.Count != 1 ||
                    !bool.TryParse(query.Value[0], out var recursive) ||
                    recursive)
                    return false;
                continue;
            }

            if (!IsNativeMusicRootQueryOption(query.Key)) return false;
        }

        return true;
    }

    private static bool IsNativeMusicRootQueryOption(string key) =>
        key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("UserId", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("StartIndex", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Limit", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Fields", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("EnableImages", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("EnableUserData", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("EnableTotalRecordCount", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ImageTypeLimit", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("EnableImageTypes", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SortBy", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SortOrder", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("CollapseBoxSetItems", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ExcludeLocationTypes", StringComparison.OrdinalIgnoreCase);

    private async Task<IActionResult> MergeVirtualPlaylistBrowseAsync(
        JsonDocument backendResult,
        int limit,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(backendResult.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
        var items = root["Items"] as JsonArray;
        if (items == null)
        {
            items = [];
            root["Items"] = items;
        }
        var backendTotal = root["TotalRecordCount"]?.GetValue<int>() ?? items.Count;
        var context = HttpContext.RequireProtocolExecutionContext();
        var virtualPlaylists = await _virtualPlaylistProtocolAdapter.ListAsync(context, cancellationToken);
        var aliasedProtocolIds = new HashSet<string>(StringComparer.Ordinal);
        var byTargetId = virtualPlaylists
            .Where(item => !string.IsNullOrWhiteSpace(item.TargetPlaylistId))
            .GroupBy(item => item.TargetPlaylistId!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        foreach (var playlist in byTargetId.Values)
            aliasedProtocolIds.Add(playlist.ProtocolId);
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["Id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
                !byTargetId.TryGetValue(id, out var playlist)) continue;
            item["Name"] = playlist.Name;
            item["Overview"] = playlist.Description;
            item["ChildCount"] = playlist.Tracks.Count;
            item["RunTimeTicks"] = playlist.Tracks.Sum(track => track.DurationMilliseconds) *
                                   TimeSpan.TicksPerMillisecond;
        }
        if (_spotifySettings.Enabled)
        {
            var bySourceId = virtualPlaylists
                .Where(item => item.SourceProviderId.Equals("spotify", StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => item.SourcePlaylistId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var aliases = _spotifySettings.Playlists
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.JellyfinId) &&
                    (string.IsNullOrWhiteSpace(item.UserId) ||
                     item.UserId.Equals(
                         context.VerifiedBackendPrincipalId,
                         StringComparison.OrdinalIgnoreCase)) &&
                    bySourceId.ContainsKey(item.Id))
                .GroupBy(item => item.JellyfinId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => bySourceId[group.First().Id],
                    StringComparer.OrdinalIgnoreCase);
            foreach (var playlist in aliases.Values)
                aliasedProtocolIds.Add(playlist.ProtocolId);

            for (var index = 0; index < items.Count; index++)
            {
                if (items[index] is not JsonObject item ||
                    item["Id"] is not JsonValue idValue ||
                    !idValue.TryGetValue<string>(out var id) ||
                    !aliases.TryGetValue(id, out var playlist))
                    continue;

                item["Name"] = playlist.Name;
                item["Overview"] = playlist.Description;
                item["ChildCount"] = playlist.Tracks.Count;
                item["RunTimeTicks"] = playlist.Tracks.Sum(track => track.DurationMilliseconds) *
                                       TimeSpan.TicksPerMillisecond;
                var providerIds = item["ProviderIds"] as JsonObject ?? [];
                item["ProviderIds"] = providerIds;
                providerIds["AllstarrSource"] = playlist.SourceProviderId;
            }
        }

        var appendable = virtualPlaylists
            .Where(item => !aliasedProtocolIds.Contains(item.ProtocolId))
            .ToArray();
        var virtualPage = GetVirtualPlaylistPage(backendTotal, items.Count, startIndex, limit);
        foreach (var playlist in appendable.Skip(virtualPage.Start).Take(virtualPage.Take))
            items.Add(JsonSerializer.SerializeToNode(
                _virtualPlaylistProtocolAdapter.ToItem(playlist)));
        root["TotalRecordCount"] = virtualPage.BackendTotal + appendable.Length;
        root["StartIndex"] = startIndex;
        return Content(root.ToJsonString(), "application/json");
    }

    internal static (int BackendTotal, int Start, int Take) GetVirtualPlaylistPage(
        int backendTotal,
        int backendReturned,
        int startIndex,
        int limit)
    {
        var requestedLimit = Math.Max(0, limit);
        var backendEnd = startIndex + backendReturned;
        var effectiveBackendTotal = backendReturned < requestedLimit
            ? Math.Min(backendTotal, backendEnd)
            : backendTotal;
        return backendEnd < effectiveBackendTotal || backendReturned >= requestedLimit
            ? (effectiveBackendTotal, 0, 0)
            : (effectiveBackendTotal, Math.Max(0, backendEnd - effectiveBackendTotal),
                requestedLimit - backendReturned);
    }

    #endregion
}
