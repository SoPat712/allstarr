using System.Text.Json;
using System.Text;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Search

    /// <summary>
    /// Searches local Jellyfin library and external providers.
    /// Combines songs/albums/artists. Works with /Items and /Users/{userId}/Items.
    /// </summary>
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
        [FromQuery] string? albumIds = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool recursive = true,
        string? userId = null)
    {
        var boundSearchTerm = searchTerm;
        searchTerm = GetEffectiveSearchTerm(searchTerm, Request.QueryString.Value);
        string? searchCacheKey = null;

        // AlbumArtistIds takes precedence over ArtistIds if both are provided
        var effectiveArtistIds = albumArtistIds ?? artistIds;
        var favoritesOnlyRequest = IsFavoritesOnlyRequest();

        _logger.LogDebug("=== SEARCHITEMS V2 CALLED === searchTerm={SearchTerm}, includeItemTypes={ItemTypes}, parentId={ParentId}, artistIds={ArtistIds}, albumArtistIds={AlbumArtistIds}, albumIds={AlbumIds}, userId={UserId}",
            searchTerm, includeItemTypes, parentId, artistIds, albumArtistIds, albumIds, userId);
        _logger.LogInformation(
            "SEARCH TRACE: rawQuery='{RawQuery}', boundSearchTerm='{BoundSearchTerm}', effectiveSearchTerm='{EffectiveSearchTerm}', includeItemTypes='{IncludeItemTypes}'",
            Request.QueryString.Value ?? string.Empty,
            boundSearchTerm ?? string.Empty,
            searchTerm ?? string.Empty,
            includeItemTypes ?? string.Empty);

        // ============================================================================
        // REQUEST ROUTING LOGIC (Priority Order)
        // ============================================================================
        // 1. ArtistIds present (external) → Handle external artists (even with ParentId)
        // 2. AlbumIds present (external) → Handle external albums (even with ParentId)
        // 3. ParentId present → GetChildItems (handles external playlists/albums/artists OR proxies library items)
        // 4. ArtistIds present (library) → Proxy to Jellyfin with artist filter
        // 5. SearchTerm present → Integrated search (Jellyfin + external sources)
        // 6. Otherwise → Proxy browse request transparently to Jellyfin
        // ============================================================================

        // PRIORITY 1: External artist filter - takes precedence over everything (including ParentId)
        if (!string.IsNullOrWhiteSpace(effectiveArtistIds))
        {
            var artistId = effectiveArtistIds.Split(',')[0]; // Take first artist if multiple
            var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(artistId);

            if (isExternal)
            {
                if (favoritesOnlyRequest)
                {
                    _logger.LogDebug("Suppressing external artist results for favorites-only request: {ArtistId}", artistId);
                    return CreateEmptyItemsResponse(startIndex);
                }

                // Check if this is a curator ID (format: ext-{provider}-curator-{name})
                if (artistId.Contains("-curator-", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Fetching playlists for curator: {ArtistId}", artistId);
                    return await GetCuratorPlaylists(provider!, externalId!, includeItemTypes, HttpContext.RequestAborted);
                }

                _logger.LogDebug("Fetching content for external artist: {Provider}/{ExternalId}, type={Type}, parentId={ParentId}",
                    provider, externalId, type, parentId);
                return await GetExternalChildItems(provider!, type!, externalId!, includeItemTypes, HttpContext.RequestAborted);
            }
            // If library artist, fall through to handle with ParentId or proxy
        }

        // PRIORITY 2: External album filter
        if (!string.IsNullOrWhiteSpace(albumIds))
        {
            var albumId = albumIds.Split(',')[0]; // Take first album if multiple
            var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(albumId);

            if (isExternal)
            {
                if (favoritesOnlyRequest)
                {
                    _logger.LogDebug("Suppressing external album results for favorites-only request: {AlbumId}", albumId);
                    return CreateEmptyItemsResponse(startIndex);
                }

                _logger.LogDebug("Fetching songs for external album: {Provider}/{ExternalId}", provider,
                    externalId);

                var album = await _metadataService.GetAlbumAsync(provider!, externalId!, HttpContext.RequestAborted);
                if (album == null)
                {
                    return new JsonResult(new
                        { Items = Array.Empty<object>(), TotalRecordCount = 0, StartIndex = startIndex });
                }

                var albumItems = album.Songs.Select(song => _responseBuilder.ConvertSongToJellyfinItem(song)).ToList();

                return new JsonResult(new
                {
                    Items = albumItems,
                    TotalRecordCount = albumItems.Count,
                    StartIndex = startIndex
                });
            }
            // If library album, fall through to handle with ParentId or proxy
        }

        // PRIORITY 3: ParentId present - check if external first
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            // Check if this is an external playlist
            if (PlaylistIdHelper.IsExternalPlaylist(parentId))
            {
                return await GetPlaylistTracks(parentId);
            }

            var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(parentId);

            if (isExternal)
            {
                if (favoritesOnlyRequest)
                {
                    _logger.LogDebug("Suppressing external parent results for favorites-only request: {ParentId}", parentId);
                    return CreateEmptyItemsResponse(startIndex);
                }

                // External parent - get external content
                _logger.LogDebug("Fetching children for external parent: {Provider}/{Type}/{ExternalId}",
                    provider, type, externalId);
                return await GetExternalChildItems(provider!, type!, externalId!, includeItemTypes, HttpContext.RequestAborted);
            }

            // Library ParentId - check if it's the music library root with a search term
            var isMusicLibrary = parentId == _settings.LibraryId;

            if (isMusicLibrary && !string.IsNullOrWhiteSpace(searchTerm))
            {
                _logger.LogDebug("Searching within music library {ParentId}, including external sources",
                    parentId);
                // Fall through to integrated search below
            }
            else
            {
                // Library parent - proxy the entire request to Jellyfin as-is
                _logger.LogDebug("Library ParentId detected, proxying entire request to Jellyfin");
                // Fall through to proxy logic at the end
            }
        }

        // PRIORITY 4: Library artist filter (already checked for external above)
        if (!string.IsNullOrWhiteSpace(effectiveArtistIds))
        {
            // Library artist - proxy transparently with full query string
            _logger.LogDebug("Library artist filter requested, proxying to Jellyfin");
            var endpoint = userId != null
                ? $"Users/{userId}/Items{Request.QueryString}"
                : $"Items{Request.QueryString}";
            var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
            return HandleProxyResponse(result, statusCode);
        }

        // PRIORITY 5: Search term present - do integrated search (Jellyfin + external)
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            // Check cache for search results (only cache pure searches, not filtered searches)
            if (string.IsNullOrWhiteSpace(effectiveArtistIds) && string.IsNullOrWhiteSpace(albumIds))
            {
                searchCacheKey = CacheKeyBuilder.BuildSearchKey(
                    searchTerm,
                    includeItemTypes,
                    limit,
                    startIndex,
                    parentId,
                    sortBy,
                    Request.Query["SortOrder"].ToString(),
                    recursive,
                    userId,
                    Request.Query["IsFavorite"].ToString());
                var cachedResult = await _cache.GetStringAsync(searchCacheKey);

                if (!string.IsNullOrWhiteSpace(cachedResult))
                {
                    _logger.LogInformation("SEARCH TRACE: cache hit for key '{CacheKey}'", searchCacheKey);
                    return Content(cachedResult, "application/json");
                }
            }

            // Fall through to integrated search below
        }
        // PRIORITY 6: No filters, no search - proxy browse request transparently
        else
        {
            _logger.LogDebug("Browse request with no filters, proxying to Jellyfin with full query string");

            var endpoint = userId != null ? $"Users/{userId}/Items" : "Items";

            // Include MediaSources only for audio-oriented browse requests (bitrate needs).
            // Album/artist browse requests should stay as close to raw Jellyfin responses as possible.
            var queryString = Request.QueryString.Value ?? "";
            var requestedTypes = ParseItemTypes(includeItemTypes);
            var shouldIncludeMediaSources = requestedTypes != null &&
                                            requestedTypes.Contains("Audio", StringComparer.OrdinalIgnoreCase);

            if (shouldIncludeMediaSources && !string.IsNullOrEmpty(queryString))
            {
                // Parse query string to modify Fields parameter
                var queryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);

                if (queryParams.ContainsKey("Fields"))
                {
                    var fieldsValue = queryParams["Fields"].ToString();
                    if (!fieldsValue.Contains("MediaSources", StringComparison.OrdinalIgnoreCase))
                    {
                        // Append MediaSources to existing Fields
                        var newFields = string.IsNullOrEmpty(fieldsValue)
                            ? "MediaSources"
                            : $"{fieldsValue},MediaSources";

                        // Rebuild query string with updated Fields
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
                    // No Fields parameter, add it
                    queryString = $"{queryString}&Fields=MediaSources";
                }
            }
            else if (shouldIncludeMediaSources)
            {
                // No query string at all
                queryString = "?Fields=MediaSources";
            }

            if (!string.IsNullOrEmpty(queryString))
            {
                endpoint = $"{endpoint}{queryString}";
            }

            var (browseResult, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

            // If Jellyfin returned an error, pass it through unchanged
            if (browseResult == null)
            {
                _logger.LogDebug("Jellyfin returned {StatusCode}, passing through to client", statusCode);
                return HandleProxyResponse(browseResult, statusCode);
            }

            // Update Spotify playlist counts if enabled and response contains playlists
            if (ShouldProcessSpotifyPlaylistCounts(browseResult, includeItemTypes))
            {
                _logger.LogDebug("Browse result has Items, checking for Spotify playlists to update counts");
                browseResult = await UpdateSpotifyPlaylistCounts(browseResult);
            }

            var result = JsonSerializer.Deserialize<object>(browseResult.RootElement.GetRawText());
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var rawText = browseResult.RootElement.GetRawText();
                var preview = rawText.Length > 200 ? rawText[..200] : rawText;
                _logger.LogDebug("Jellyfin browse result preview: {Result}", preview);
            }

            return new JsonResult(result);
        }

        // ============================================================================
        // INTEGRATED SEARCH: Search both Jellyfin library and external sources
        // ============================================================================

        var cleanQuery = searchTerm?.Trim().Trim('"') ?? "";
        _logger.LogDebug("Performing integrated search for: {Query}", cleanQuery);

        // Run local and external searches in parallel
        var itemTypes = ParseItemTypes(includeItemTypes);
        var jellyfinTask = GetLocalSearchResultForCurrentRequest(
            cleanQuery,
            includeItemTypes,
            limit,
            startIndex,
            recursive,
            userId);

        // Use parallel metadata service if available (races providers), otherwise use primary
        var externalTask = favoritesOnlyRequest
            ? Task.FromResult(new SearchResult())
            : _parallelMetadataService != null
                ? _parallelMetadataService.SearchAllAsync(cleanQuery, limit, limit, limit, HttpContext.RequestAborted)
                : _metadataService.SearchAllAsync(cleanQuery, limit, limit, limit, HttpContext.RequestAborted);

        var playlistTask = favoritesOnlyRequest || !_settings.EnableExternalPlaylists
            ? Task.FromResult(new List<ExternalPlaylist>())
            : _metadataService.SearchPlaylistsAsync(cleanQuery, limit, HttpContext.RequestAborted);

        _logger.LogDebug("Playlist search enabled: {Enabled}, searching for: '{Query}'",
            _settings.EnableExternalPlaylists, cleanQuery);

        await Task.WhenAll(jellyfinTask, externalTask, playlistTask);

        var (jellyfinResult, _) = await jellyfinTask;
        var externalResult = await externalTask;
        var playlistResult = await playlistTask;

        _logger.LogDebug(
            "Search results for '{Query}': Jellyfin={JellyfinCount}, External Songs={ExtSongs}, Albums={ExtAlbums}, Artists={ExtArtists}, Playlists={Playlists}",
            cleanQuery,
            jellyfinResult != null ? "found" : "null",
            externalResult.Songs.Count,
            externalResult.Albums.Count,
            externalResult.Artists.Count,
            playlistResult.Count);

        // Keep raw Jellyfin items for local tracks (preserves ALL metadata!)
        var jellyfinSongItems = new List<Dictionary<string, object?>>();
        var jellyfinAlbumItems = new List<Dictionary<string, object?>>();
        var jellyfinArtistItems = new List<Dictionary<string, object?>>();

        if (jellyfinResult != null && jellyfinResult.RootElement.TryGetProperty("Items", out var jellyfinItems))
        {
            foreach (var item in jellyfinItems.EnumerateArray())
            {
                if (!item.TryGetProperty("Type", out var typeEl)) continue;
                var type = typeEl.GetString();
                var itemDict = JsonElementToDictionary(item);

                if (type == "Audio")
                {
                    jellyfinSongItems.Add(itemDict);
                }
                else if (type == "MusicAlbum")
                {
                    jellyfinAlbumItems.Add(itemDict);
                }
                else if (type == "MusicArtist")
                {
                    jellyfinArtistItems.Add(itemDict);
                }
            }
        }

        var localAlbumNamesPreview = string.Join(" | ", jellyfinAlbumItems
            .Take(10)
            .Select(GetItemName));
        _logger.LogInformation(
            "SEARCH TRACE: Jellyfin local counts for query '{Query}' => songs={SongCount}, albums={AlbumCount}, artists={ArtistCount}; localAlbumPreview=[{AlbumPreview}]",
            cleanQuery,
            jellyfinSongItems.Count,
            jellyfinAlbumItems.Count,
            jellyfinArtistItems.Count,
            localAlbumNamesPreview);

        // Convert external results to Jellyfin format
        var externalSongItems = externalResult.Songs.Select(s => _responseBuilder.ConvertSongToJellyfinItem(s)).ToList();
        var externalAlbumItems = externalResult.Albums.Select(a => _responseBuilder.ConvertAlbumToJellyfinItem(a)).ToList();
        var externalArtistItems = externalResult.Artists.Select(a => _responseBuilder.ConvertArtistToJellyfinItem(a)).ToList();

        // Keep Jellyfin/provider ordering intact.
        // Scores only decide which source leads each interleaving round.
        var allSongs = InterleaveByScore(jellyfinSongItems, externalSongItems, cleanQuery, primaryBoost: 5.0);
        var allAlbums = InterleaveByScore(jellyfinAlbumItems, externalAlbumItems, cleanQuery, primaryBoost: 5.0);
        var allArtists = InterleaveByScore(jellyfinArtistItems, externalArtistItems, cleanQuery, primaryBoost: 5.0);

        // Log top results for debugging
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            if (allSongs.Any())
            {
                var topSong = allSongs.First();
                var topName = topSong.TryGetValue("Name", out var nameObj) && nameObj is JsonElement nameEl ? nameEl.GetString() ?? "" : topSong["Name"]?.ToString() ?? "";
                _logger.LogDebug("🎵 Top song: '{Title}' (local={IsLocal})",
                    topName, IsLocalItem(topSong));
            }

            if (allAlbums.Any())
            {
                var topAlbum = allAlbums.First();
                var topName = topAlbum.TryGetValue("Name", out var nameObj) && nameObj is JsonElement nameEl ? nameEl.GetString() ?? "" : topAlbum["Name"]?.ToString() ?? "";
                _logger.LogDebug("💿 Top album: '{Title}' (local={IsLocal})",
                    topName, IsLocalItem(topAlbum));
            }

            if (allArtists.Any())
            {
                var topArtist = allArtists.First();
                var topName = topArtist.TryGetValue("Name", out var nameObj) && nameObj is JsonElement nameEl ? nameEl.GetString() ?? "" : topArtist["Name"]?.ToString() ?? "";
                _logger.LogDebug("🎤 Top artist: '{Name}' (local={IsLocal})",
                    topName, IsLocalItem(topArtist));
            }
        }

        // Add playlists (mixed with albums due to Jellyfin API limitations)
        // Playlists are converted to album format for compatibility
        var mergedPlaylistItems = new List<Dictionary<string, object?>>();
        if (playlistResult.Count > 0)
        {
            _logger.LogDebug("Processing {Count} playlists for merging with albums", playlistResult.Count);
            foreach (var playlist in playlistResult)
            {
                var playlistItem = _responseBuilder.ConvertPlaylistToAlbumItem(playlist);
                mergedPlaylistItems.Add(playlistItem);
            }

            _logger.LogDebug("Found {Count} playlists, merging with albums", playlistResult.Count);
        }
        else
        {
            _logger.LogDebug("No playlists found to merge with albums");
        }

        // Keep album/playlist source ordering intact and only let scores decide who leads each round.
        var mergedAlbumsAndPlaylists = InterleaveByScore(allAlbums, mergedPlaylistItems, cleanQuery, primaryBoost: 0.0);

        _logger.LogDebug(
            "Merged results: Songs={Songs}, Albums+Playlists={AlbumsPlaylists}, Artists={Artists}",
            allSongs.Count, mergedAlbumsAndPlaylists.Count, allArtists.Count);

        // Pre-fetch lyrics for top 3 LOCAL songs in background (don't await)
        // Skip external tracks to avoid spamming LRCLIB with malformed titles
        if (_lrclibService != null && allSongs.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var top3Local = allSongs.Where(IsLocalItem).Take(3).ToList();
                    if (top3Local.Count > 0)
                    {
                        _logger.LogDebug("🎵 Pre-fetching lyrics for top {Count} LOCAL search results", top3Local.Count);

                        foreach (var songItem in top3Local)
                        {
                            var title = songItem.TryGetValue("Name", out var nameObj) && nameObj is JsonElement nameEl ? nameEl.GetString() ?? "" : songItem["Name"]?.ToString() ?? "";
                            var artist = "";

                            if (songItem.TryGetValue("Artists", out var artistsObj) && artistsObj is JsonElement artistsEl && artistsEl.GetArrayLength() > 0)
                            {
                                artist = artistsEl[0].GetString() ?? "";
                            }
                            else if (songItem.TryGetValue("Artists", out var artistsListObj) && artistsListObj is object[] artistsList && artistsList.Length > 0)
                            {
                                artist = artistsList[0]?.ToString() ?? "";
                            }

                            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
                            {
                                await _lrclibService.GetLyricsAsync(title, artist, "", 0);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to pre-fetch lyrics for search results");
                }
            });
        }

        // Filter by item types if specified
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

        var includesSongs = itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("Audio");
        var includesAlbums = itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("MusicAlbum") || itemTypes.Contains("Playlist");
        var includesArtists = itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("MusicArtist");

        var externalHasRequestedTypeResults =
            (includesSongs && externalSongItems.Count > 0) ||
            (includesAlbums && (externalAlbumItems.Count > 0 || mergedPlaylistItems.Count > 0)) ||
            (includesArtists && externalArtistItems.Count > 0);

        // Apply pagination
        var pagedItems = items.Skip(startIndex).Take(limit).ToList();

        _logger.LogDebug("Returning {Count} items (total: {Total})", pagedItems.Count, items.Count);

        try
        {
            // Return with PascalCase - use ContentResult to bypass JSON serialization issues
            var response = new
            {
                Items = pagedItems,
                TotalRecordCount = items.Count,
                StartIndex = startIndex
            };
            var json = SerializeSearchResponseJson(response);

            // Cache search results in Redis using the configured search TTL.
            if (!string.IsNullOrWhiteSpace(searchTerm) &&
                string.IsNullOrWhiteSpace(effectiveArtistIds) &&
                !string.IsNullOrWhiteSpace(searchCacheKey))
            {
                if (externalHasRequestedTypeResults)
                {
                    await _cache.SetStringAsync(searchCacheKey, json, CacheExtensions.SearchResultsTTL);
                    _logger.LogDebug("💾 Cached search results for '{SearchTerm}' ({Minutes} min TTL)", searchTerm,
                        CacheExtensions.SearchResultsTTL.TotalMinutes);
                }
                else
                {
                    _logger.LogInformation(
                        "SEARCH TRACE: skipped cache write for query '{Query}' because requested external result buckets were empty (types={ItemTypes})",
                        cleanQuery,
                        includeItemTypes ?? string.Empty);
                }
            }

            _logger.LogDebug("About to serialize response...");

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var preview = json.Length > 200 ? json[..200] : json;
                _logger.LogDebug("JSON response preview: {Json}", preview);
            }

            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serializing search response");
            throw;
        }
    }

    private static string SerializeSearchResponseJson<T>(T response) where T : class
    {
        return JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null
        });
    }

    /// <summary>
    /// Gets child items of a parent (tracks in album, albums for artist).
    /// </summary>
    private async Task<IActionResult> GetChildItems(
        string parentId,
        string? includeItemTypes,
        int limit,
        int startIndex,
        string? sortBy)
    {
        // Check if this is an external playlist
        if (PlaylistIdHelper.IsExternalPlaylist(parentId))
        {
            return await GetPlaylistTracks(parentId);
        }

        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(parentId);

        if (isExternal)
        {
            // Get external album or artist content
            return await GetExternalChildItems(provider!, type!, externalId!, includeItemTypes);
        }

        // For library items, proxy transparently with full query string
        _logger.LogDebug("Proxying library item request to Jellyfin: ParentId={ParentId}", parentId);

        // Build endpoint - handle both /Items and /Users/{userId}/Items routes
        var userIdFromRoute = Request.RouteValues["userId"]?.ToString();
        var endpoint = string.IsNullOrEmpty(userIdFromRoute)
            ? $"Items{Request.QueryString}"
            : $"Users/{userIdFromRoute}/Items{Request.QueryString}";

        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

        return HandleProxyResponse(result, statusCode);
    }

    private async Task<(JsonDocument? Body, int StatusCode)> GetLocalSearchResultForCurrentRequest(
        string cleanQuery,
        string? includeItemTypes,
        int limit,
        int startIndex,
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

        // Preserve literal request semantics, only normalize recovered SearchTerm.
        queryParams["SearchTerm"] = cleanQuery;

        _logger.LogInformation(
            "SEARCH TRACE: local proxy request endpoint='{Endpoint}' query='{SafeQuery}'",
            endpoint,
            ToSafeQueryStringForLogs(queryParams));

        return await _proxyService.GetJsonAsync(endpoint, queryParams, Request.Headers);
    }

    /// <summary>
    /// Quick search endpoint. Works with /Search/Hints and /Users/{userId}/Search/Hints.
    /// </summary>
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

        // Use parallel metadata service if available (races providers), otherwise use primary
        var externalTask = _parallelMetadataService != null
            ? _parallelMetadataService.SearchAllAsync(cleanQuery, limit, limit, limit, HttpContext.RequestAborted)
            : _metadataService.SearchAllAsync(cleanQuery, limit, limit, limit, HttpContext.RequestAborted);

        // Run searches in parallel (local Jellyfin hints + external providers)
        var jellyfinTask = GetLocalSearchHintsResultForCurrentRequest(cleanQuery, userId);

        await Task.WhenAll(jellyfinTask, externalTask);

        var (jellyfinResult, _) = await jellyfinTask;
        var externalResult = await externalTask;

        var (localSongs, localAlbums, localArtists) = _modelMapper.ParseSearchHintsResponse(jellyfinResult);

        // NO deduplication - merge all results and take top matches
        var allSongs = localSongs.Concat(externalResult.Songs).Take(limit).ToList();
        var allAlbums = localAlbums.Concat(externalResult.Albums).Take(limit).ToList();
        var allArtists = localArtists.Concat(externalResult.Artists).Take(limit).ToList();

        return _responseBuilder.CreateSearchHintsResponse(
            allSongs.Take(limit).ToList(),
            allAlbums.Take(limit).ToList(),
            allArtists.Take(limit).ToList());
    }

    private async Task<(JsonDocument? Body, int StatusCode)> GetLocalSearchHintsResultForCurrentRequest(
        string cleanQuery,
        string? userId)
    {
        var endpoint = !string.IsNullOrWhiteSpace(userId)
            ? $"Users/{userId}/Search/Hints"
            : "Search/Hints";

        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in Request.Query)
        {
            queryParams[kvp.Key] = kvp.Value.ToString();
        }

        // Preserve literal request semantics, only normalize recovered SearchTerm.
        queryParams["SearchTerm"] = cleanQuery;

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
        return string.Equals(Request.Query["IsFavorite"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static IActionResult CreateEmptyItemsResponse(int startIndex)
    {
        return new JsonResult(new
        {
            Items = Array.Empty<object>(),
            TotalRecordCount = 0,
            StartIndex = startIndex
        });
    }

    /// <summary>
    /// Merges two source queues without reordering either queue.
    /// At each step, compare only the current head from each source and dequeue the winner.
    /// </summary>
    private List<Dictionary<string, object?>> InterleaveByScore(
        List<Dictionary<string, object?>> primaryItems,
        List<Dictionary<string, object?>> secondaryItems,
        string query,
        double primaryBoost)
    {
        var primaryScored = primaryItems.Select(item =>
        {
            return new
            {
                Item = item,
                Score = Math.Min(100.0, CalculateItemRelevanceScore(query, item) + primaryBoost)
            };
        })
        .ToList();

        var secondaryScored = secondaryItems.Select(item =>
        {
            return new
            {
                Item = item,
                Score = CalculateItemRelevanceScore(query, item)
            };
        })
        .ToList();

        var result = new List<Dictionary<string, object?>>(primaryScored.Count + secondaryScored.Count);
        int primaryIdx = 0, secondaryIdx = 0;

        while (primaryIdx < primaryScored.Count && secondaryIdx < secondaryScored.Count)
        {
            var primaryCandidate = primaryScored[primaryIdx];
            var secondaryCandidate = secondaryScored[secondaryIdx];

            if (primaryCandidate.Score >= secondaryCandidate.Score)
            {
                result.Add(primaryScored[primaryIdx++].Item);
            }
            else
            {
                result.Add(secondaryScored[secondaryIdx++].Item);
            }
        }

        while (primaryIdx < primaryScored.Count)
        {
            result.Add(primaryScored[primaryIdx++].Item);
        }

        while (secondaryIdx < secondaryScored.Count)
        {
            result.Add(secondaryScored[secondaryIdx++].Item);
        }

        return result;
    }

    /// <summary>
    /// Calculates query relevance using the product's per-type rules.
    /// </summary>
    private double CalculateItemRelevanceScore(string query, Dictionary<string, object?> item)
    {
        return GetItemType(item) switch
        {
            "Audio" => CalculateSongRelevanceScore(query, item),
            "MusicAlbum" => CalculateAlbumRelevanceScore(query, item),
            "MusicArtist" => CalculateArtistRelevanceScore(query, item),
            _ => CalculateArtistRelevanceScore(query, item)
        };
    }

    /// <summary>
    /// Extracts the name/title from a Jellyfin item dictionary.
    /// </summary>
    private string GetItemName(Dictionary<string, object?> item)
    {
        return GetItemStringValue(item, "Name");
    }

    private double CalculateSongRelevanceScore(string query, Dictionary<string, object?> item)
    {
        var title = GetItemName(item);
        var artistText = GetSongArtistText(item);
        return CalculateBestFuzzyScore(query, title, CombineSearchFields(title, artistText));
    }

    private double CalculateAlbumRelevanceScore(string query, Dictionary<string, object?> item)
    {
        var albumName = GetItemName(item);
        var artistText = GetAlbumArtistText(item);
        return CalculateBestFuzzyScore(query, albumName, CombineSearchFields(albumName, artistText));
    }

    private double CalculateArtistRelevanceScore(string query, Dictionary<string, object?> item)
    {
        var artistName = GetItemName(item);
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return 0;
        }

        return FuzzyMatcher.CalculateSimilarityAggressive(query, artistName);
    }

    private double CalculateBestFuzzyScore(string query, params string?[] candidates)
    {
        var best = 0;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            best = Math.Max(best, FuzzyMatcher.CalculateSimilarityAggressive(query, candidate));
        }

        return best;
    }

    private static string CombineSearchFields(params string?[] fields)
    {
        return string.Join(" ", fields.Where(field => !string.IsNullOrWhiteSpace(field)));
    }

    private string GetItemType(Dictionary<string, object?> item)
    {
        return GetItemStringValue(item, "Type");
    }

    private string GetSongArtistText(Dictionary<string, object?> item)
    {
        var artists = GetItemStringList(item, "Artists").Take(3).ToList();
        if (artists.Count > 0)
        {
            return string.Join(" ", artists);
        }

        var albumArtist = GetItemStringValue(item, "AlbumArtist");
        if (!string.IsNullOrWhiteSpace(albumArtist))
        {
            return albumArtist;
        }

        return GetItemStringValue(item, "Artist");
    }

    private string GetAlbumArtistText(Dictionary<string, object?> item)
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

    private string GetItemStringValue(Dictionary<string, object?> item, string key)
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

    private IEnumerable<string> GetItemStringList(Dictionary<string, object?> item, string key)
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

    #endregion
}
