using System.Text.Json;
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
        // AlbumArtistIds takes precedence over ArtistIds if both are provided
        var effectiveArtistIds = albumArtistIds ?? artistIds;

        _logger.LogDebug(
            "=== SEARCHITEMS V2 CALLED === searchTerm={SearchTerm}, includeItemTypes={ItemTypes}, parentId={ParentId}, artistIds={ArtistIds}, albumArtistIds={AlbumArtistIds}, albumIds={AlbumIds}, userId={UserId}",
            searchTerm, includeItemTypes, parentId, artistIds, albumArtistIds, albumIds, userId);

        // ============================================================================
        // REQUEST ROUTING LOGIC (Priority Order)
        // ============================================================================
        // 1. ParentId present → GetChildItems (handles external playlists/albums/artists OR proxies library items)
        // 2. AlbumIds present → Handle external albums OR proxy library albums
        // 3. ArtistIds present → Handle external artists OR proxy library artists  
        // 4. SearchTerm present → Integrated search (Jellyfin + external sources)
        // 5. Otherwise → Proxy browse request transparently to Jellyfin
        // ============================================================================

        // PRIORITY 1: ParentId takes precedence - handles both external and library items
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            // Check if this is the music library root with a search term - if so, do integrated search
            var isMusicLibrary = parentId == _settings.LibraryId;

            if (isMusicLibrary && !string.IsNullOrWhiteSpace(searchTerm))
            {
                _logger.LogInformation("Searching within music library {ParentId}, including external sources",
                    parentId);
                // Fall through to integrated search below
            }
            else
            {
                // Browse parent item (external playlist/album/artist OR library item)
                _logger.LogDebug("Browsing parent: {ParentId}", parentId);
                return await GetChildItems(parentId, includeItemTypes, limit, startIndex, sortBy);
            }
        }

        // PRIORITY 2: Filter by album (no parentId)
        if (string.IsNullOrWhiteSpace(parentId) && !string.IsNullOrWhiteSpace(albumIds))
        {
            var albumId = albumIds.Split(',')[0]; // Take first album if multiple
            var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(albumId);

            if (isExternal)
            {
                _logger.LogInformation("Fetching songs for external album: {Provider}/{ExternalId}", provider,
                    externalId);

                var album = await _metadataService.GetAlbumAsync(provider!, externalId!);
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
            else
            {
                // Library album - proxy transparently with full query string
                _logger.LogDebug("Library album filter requested: {AlbumId}, proxying to Jellyfin", albumId);
                var endpoint = userId != null
                    ? $"Users/{userId}/Items{Request.QueryString}"
                    : $"Items{Request.QueryString}";
                var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
                return HandleProxyResponse(result, statusCode);
            }
        }

        // PRIORITY 3: Filter by artist (no parentId, no albumIds)
        if (string.IsNullOrWhiteSpace(parentId) && string.IsNullOrWhiteSpace(albumIds) &&
            !string.IsNullOrWhiteSpace(effectiveArtistIds))
        {
            var artistId = effectiveArtistIds.Split(',')[0]; // Take first artist if multiple
            var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(artistId);

            if (isExternal)
            {
                // Check if this is a curator ID (format: ext-{provider}-curator-{name})
                if (artistId.Contains("-curator-", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Fetching playlists for curator: {ArtistId}", artistId);
                    return await GetCuratorPlaylists(provider!, externalId!, includeItemTypes);
                }

                _logger.LogInformation("Fetching content for external artist: {Provider}/{ExternalId}", provider,
                    externalId);
                return await GetExternalChildItems(provider!, externalId!, includeItemTypes);
            }
            else
            {
                // Library artist - proxy transparently with full query string
                _logger.LogDebug("Library artist filter requested: {ArtistId}, proxying to Jellyfin", artistId);
                var endpoint = userId != null
                    ? $"Users/{userId}/Items{Request.QueryString}"
                    : $"Items{Request.QueryString}";
                var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
                return HandleProxyResponse(result, statusCode);
            }
        }

        // PRIORITY 4: Search term present - do integrated search (Jellyfin + external)
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            // Check cache for search results (only cache pure searches, not filtered searches)
            if (string.IsNullOrWhiteSpace(effectiveArtistIds) && string.IsNullOrWhiteSpace(albumIds))
            {
                var cacheKey = CacheKeyBuilder.BuildSearchKey(searchTerm, includeItemTypes, limit, startIndex);
                var cachedResult = await _cache.GetAsync<object>(cacheKey);

                if (cachedResult != null)
                {
                    _logger.LogDebug("✅ Returning cached search results for '{SearchTerm}'", searchTerm);
                    return new JsonResult(cachedResult);
                }
            }

            // Fall through to integrated search below
        }
        // PRIORITY 5: No filters, no search - proxy browse request transparently
        else
        {
            _logger.LogDebug("Browse request with no filters, proxying to Jellyfin with full query string");

            var endpoint = userId != null ? $"Users/{userId}/Items" : "Items";

            // Ensure MediaSources is included in Fields parameter for bitrate info
            var queryString = Request.QueryString.Value ?? "";

            if (!string.IsNullOrEmpty(queryString))
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
            else
            {
                // No query string at all
                queryString = "?Fields=MediaSources";
            }

            endpoint = $"{endpoint}{queryString}";

            var (browseResult, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

            if (browseResult == null)
            {
                if (statusCode == 401)
                {
                    _logger.LogInformation("Jellyfin returned 401 Unauthorized, returning 401 to client");
                    return Unauthorized(new { error = "Authentication required" });
                }

                _logger.LogDebug("Jellyfin returned {StatusCode}, returning empty result", statusCode);
                return new JsonResult(new
                    { Items = Array.Empty<object>(), TotalRecordCount = 0, StartIndex = startIndex });
            }

            // Update Spotify playlist counts if enabled and response contains playlists
            if (_spotifySettings.Enabled && browseResult.RootElement.TryGetProperty("Items", out var _))
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
        var jellyfinTask = _proxyService.SearchAsync(cleanQuery, itemTypes, limit, recursive, Request.Headers);

        // Use parallel metadata service if available (races providers), otherwise use primary
        var externalTask = _parallelMetadataService != null
            ? _parallelMetadataService.SearchAllAsync(cleanQuery, limit, limit, limit)
            : _metadataService.SearchAllAsync(cleanQuery, limit, limit, limit);

        var playlistTask = _settings.EnableExternalPlaylists
            ? _metadataService.SearchPlaylistsAsync(cleanQuery, limit)
            : Task.FromResult(new List<ExternalPlaylist>());

        _logger.LogDebug("Playlist search enabled: {Enabled}, searching for: '{Query}'",
            _settings.EnableExternalPlaylists, cleanQuery);

        await Task.WhenAll(jellyfinTask, externalTask, playlistTask);

        var (jellyfinResult, _) = await jellyfinTask;
        var externalResult = await externalTask;
        var playlistResult = await playlistTask;

        _logger.LogInformation(
            "Search results for '{Query}': Jellyfin={JellyfinCount}, External Songs={ExtSongs}, Albums={ExtAlbums}, Artists={ExtArtists}, Playlists={Playlists}",
            cleanQuery,
            jellyfinResult != null ? "found" : "null",
            externalResult.Songs.Count,
            externalResult.Albums.Count,
            externalResult.Artists.Count,
            playlistResult.Count);

        // Parse Jellyfin results into domain models
        var (localSongs, localAlbums, localArtists) = _modelMapper.ParseItemsResponse(jellyfinResult);

        // Sort all results by match score (local tracks get +10 boost)
        // This ensures best matches appear first regardless of source
        var allSongs = localSongs.Concat(externalResult.Songs)
            .Select(s => new
                { Song = s, Score = FuzzyMatcher.CalculateSimilarity(cleanQuery, s.Title) + (s.IsLocal ? 10.0 : 0.0) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Song)
            .ToList();

        var allAlbums = localAlbums.Concat(externalResult.Albums)
            .Select(a => new
                { Album = a, Score = FuzzyMatcher.CalculateSimilarity(cleanQuery, a.Title) + (a.IsLocal ? 10.0 : 0.0) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Album)
            .ToList();

        var allArtists = localArtists.Concat(externalResult.Artists)
            .Select(a => new
                { Artist = a, Score = FuzzyMatcher.CalculateSimilarity(cleanQuery, a.Name) + (a.IsLocal ? 10.0 : 0.0) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Artist)
            .ToList();

        // Log top results for debugging
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            if (allSongs.Any())
            {
                var topSong = allSongs.First();
                var topScore = FuzzyMatcher.CalculateSimilarity(cleanQuery, topSong.Title) +
                               (topSong.IsLocal ? 10.0 : 0.0);
                _logger.LogDebug("🎵 Top song: '{Title}' (local={IsLocal}, score={Score:F2})",
                    topSong.Title, topSong.IsLocal, topScore);
            }

            if (allAlbums.Any())
            {
                var topAlbum = allAlbums.First();
                var topScore = FuzzyMatcher.CalculateSimilarity(cleanQuery, topAlbum.Title) +
                               (topAlbum.IsLocal ? 10.0 : 0.0);
                _logger.LogDebug("💿 Top album: '{Title}' (local={IsLocal}, score={Score:F2})",
                    topAlbum.Title, topAlbum.IsLocal, topScore);
            }

            if (allArtists.Any())
            {
                var topArtist = allArtists.First();
                var topScore = FuzzyMatcher.CalculateSimilarity(cleanQuery, topArtist.Name) +
                               (topArtist.IsLocal ? 10.0 : 0.0);
                _logger.LogDebug("🎤 Top artist: '{Name}' (local={IsLocal}, score={Score:F2})",
                    topArtist.Name, topArtist.IsLocal, topScore);
            }
        }

        // Convert to Jellyfin format
        var mergedSongs = allSongs.Select(s => _responseBuilder.ConvertSongToJellyfinItem(s)).ToList();
        var mergedAlbums = allAlbums.Select(a => _responseBuilder.ConvertAlbumToJellyfinItem(a)).ToList();
        var mergedArtists = allArtists.Select(a => _responseBuilder.ConvertArtistToJellyfinItem(a)).ToList();

        // Add playlists with scoring (albums get +10 boost over playlists)
        // Playlists are mixed with albums due to Jellyfin API limitations (no dedicated playlist search)
        var mergedPlaylistsWithScore = new List<(Dictionary<string, object?> Item, double Score)>();
        if (playlistResult.Count > 0)
        {
            _logger.LogInformation("Processing {Count} playlists for merging with albums", playlistResult.Count);
            foreach (var playlist in playlistResult)
            {
                var playlistItem = _responseBuilder.ConvertPlaylistToAlbumItem(playlist);
                var score = FuzzyMatcher.CalculateSimilarity(cleanQuery, playlist.Name);
                mergedPlaylistsWithScore.Add((playlistItem, score));
                _logger.LogDebug("Playlist '{Name}' score: {Score:F2}", playlist.Name, score);
            }

            _logger.LogInformation("Found {Count} playlists, merging with albums (albums get +10 score boost)",
                playlistResult.Count);
        }
        else
        {
            _logger.LogDebug("No playlists found to merge with albums");
        }

        // Merge albums and playlists, sorted by score (albums get +10 boost)
        var albumsWithScore = mergedAlbums.Select(a =>
        {
            var title = a.TryGetValue("Name", out var nameObj) && nameObj is JsonElement nameEl
                ? nameEl.GetString() ?? ""
                : "";
            var score = FuzzyMatcher.CalculateSimilarity(cleanQuery, title) + 10.0; // Albums get +10 boost
            return (Item: a, Score: score);
        });

        var mergedAlbumsAndPlaylists = albumsWithScore
            .Concat(mergedPlaylistsWithScore)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();

        _logger.LogDebug(
            "Merged and sorted results by score: Songs={Songs}, Albums+Playlists={AlbumsPlaylists}, Artists={Artists}",
            mergedSongs.Count, mergedAlbumsAndPlaylists.Count, mergedArtists.Count);

        // Pre-fetch lyrics for top 3 songs in background (don't await)
        if (_lrclibService != null && mergedSongs.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var top3 = mergedSongs.Take(3).ToList();
                    _logger.LogDebug("🎵 Pre-fetching lyrics for top {Count} search results", top3.Count);

                    foreach (var songItem in top3)
                    {
                        if (songItem.TryGetValue("Name", out var nameObj) && nameObj is JsonElement nameEl &&
                            songItem.TryGetValue("Artists", out var artistsObj) &&
                            artistsObj is JsonElement artistsEl &&
                            artistsEl.GetArrayLength() > 0)
                        {
                            var title = nameEl.GetString() ?? "";
                            var artist = artistsEl[0].GetString() ?? "";

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
            _logger.LogDebug("Adding {Count} artists to results", mergedArtists.Count);
            items.AddRange(mergedArtists);
        }

        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("MusicAlbum") ||
            itemTypes.Contains("Playlist"))
        {
            _logger.LogDebug("Adding {Count} albums+playlists to results", mergedAlbumsAndPlaylists.Count);
            items.AddRange(mergedAlbumsAndPlaylists);
        }

        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("Audio"))
        {
            _logger.LogDebug("Adding {Count} songs to results", mergedSongs.Count);
            items.AddRange(mergedSongs);
        }

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

            // Cache search results in Redis (15 min TTL, no file persistence)
            if (!string.IsNullOrWhiteSpace(searchTerm) && string.IsNullOrWhiteSpace(effectiveArtistIds))
            {
                var cacheKey = CacheKeyBuilder.BuildSearchKey(searchTerm, includeItemTypes, limit, startIndex);
                await _cache.SetAsync(cacheKey, response, CacheExtensions.SearchResultsTTL);
                _logger.LogDebug("💾 Cached search results for '{SearchTerm}' ({Minutes} min TTL)", searchTerm,
                    CacheExtensions.SearchResultsTTL.TotalMinutes);
            }

            _logger.LogDebug("About to serialize response...");

            var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                DictionaryKeyPolicy = null
            });

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

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(parentId);

        if (isExternal)
        {
            // Get external album or artist content
            return await GetExternalChildItems(provider!, externalId!, includeItemTypes);
        }

        // For library items, proxy transparently with full query string
        _logger.LogDebug("Proxying library item request to Jellyfin: ParentId={ParentId}", parentId);

        var endpoint = $"Users/{Request.RouteValues["userId"]}/Items{Request.QueryString}";
        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

        return HandleProxyResponse(result, statusCode);
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
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                SearchHints = Array.Empty<object>(),
                TotalRecordCount = 0
            });
        }

        var cleanQuery = searchTerm.Trim().Trim('"');
        var itemTypes = ParseItemTypes(includeItemTypes);

        // Run searches in parallel
        var jellyfinTask = _proxyService.SearchAsync(cleanQuery, itemTypes, limit, true, Request.Headers);
        var externalTask = _metadataService.SearchAllAsync(cleanQuery, limit, limit, limit);

        await Task.WhenAll(jellyfinTask, externalTask);

        var (jellyfinResult, _) = await jellyfinTask;
        var externalResult = await externalTask;

        var (localSongs, localAlbums, localArtists) = _modelMapper.ParseItemsResponse(jellyfinResult);

        // NO deduplication - merge all results and take top matches
        var allSongs = localSongs.Concat(externalResult.Songs).Take(limit).ToList();
        var allAlbums = localAlbums.Concat(externalResult.Albums).Take(limit).ToList();
        var allArtists = localArtists.Concat(externalResult.Artists).Take(limit).ToList();

        return _responseBuilder.CreateSearchHintsResponse(
            allSongs.Take(limit).ToList(),
            allAlbums.Take(limit).ToList(),
            allArtists.Take(limit).ToList());
    }

    #endregion
}