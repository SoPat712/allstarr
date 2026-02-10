using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using allstarr.Models.Subsonic;
using allstarr.Models.Spotify;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using allstarr.Services.Lyrics;
using allstarr.Services.Spotify;
using allstarr.Filters;

namespace allstarr.Controllers;

/// <summary>
/// Jellyfin-compatible API controller. Merges local library with external providers
/// (Deezer, Qobuz, SquidWTF). Auth goes through Jellyfin.
/// </summary>
[ApiController]
[Route("")]
public class JellyfinController : ControllerBase
{
    private readonly JellyfinSettings _settings;
    private readonly SpotifyImportSettings _spotifySettings;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly IMusicMetadataService _metadataService;
    private readonly ParallelMetadataService? _parallelMetadataService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly JellyfinResponseBuilder _responseBuilder;
    private readonly JellyfinModelMapper _modelMapper;
    private readonly JellyfinProxyService _proxyService;
    private readonly JellyfinSessionManager _sessionManager;
    private readonly PlaylistSyncService? _playlistSyncService;
    private readonly SpotifyPlaylistFetcher? _spotifyPlaylistFetcher;
    private readonly SpotifyLyricsService? _spotifyLyricsService;
    private readonly LrclibService? _lrclibService;
    private readonly OdesliService _odesliService;
    private readonly RedisCacheService _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JellyfinController> _logger;

    public JellyfinController(
        IOptions<JellyfinSettings> settings,
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        IDownloadService downloadService,
        JellyfinResponseBuilder responseBuilder,
        JellyfinModelMapper modelMapper,
        JellyfinProxyService proxyService,
        JellyfinSessionManager sessionManager,
        OdesliService odesliService,
        RedisCacheService cache,
        IConfiguration configuration,
        ILogger<JellyfinController> logger,
        ParallelMetadataService? parallelMetadataService = null,
        PlaylistSyncService? playlistSyncService = null,
        SpotifyPlaylistFetcher? spotifyPlaylistFetcher = null,
        SpotifyLyricsService? spotifyLyricsService = null,
        LrclibService? lrclibService = null)
    {
        _settings = settings.Value;
        _spotifySettings = spotifySettings.Value;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _metadataService = metadataService;
        _parallelMetadataService = parallelMetadataService;
        _localLibraryService = localLibraryService;
        _downloadService = downloadService;
        _responseBuilder = responseBuilder;
        _modelMapper = modelMapper;
        _proxyService = proxyService;
        _sessionManager = sessionManager;
        _playlistSyncService = playlistSyncService;
        _spotifyPlaylistFetcher = spotifyPlaylistFetcher;
        _spotifyLyricsService = spotifyLyricsService;
        _lrclibService = lrclibService;
        _odesliService = odesliService;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.Url))
        {
            throw new InvalidOperationException("JELLYFIN_URL environment variable is not set");
        }
    }

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
        [FromQuery] string? sortBy = null,
        [FromQuery] bool recursive = true,
        string? userId = null)
    {
        _logger.LogInformation("=== SEARCHITEMS V2 CALLED === searchTerm={SearchTerm}, includeItemTypes={ItemTypes}, parentId={ParentId}, artistIds={ArtistIds}, userId={UserId}", 
            searchTerm, includeItemTypes, parentId, artistIds, userId);

        // Cache search results in Redis only (no file persistence, 15 min TTL)
        // Only cache actual searches, not browse operations
        if (!string.IsNullOrWhiteSpace(searchTerm) && string.IsNullOrWhiteSpace(artistIds))
        {
            var cacheKey = $"search:{searchTerm?.ToLowerInvariant()}:{includeItemTypes}:{limit}:{startIndex}";
            var cachedResult = await _cache.GetAsync<object>(cacheKey);
            
            if (cachedResult != null)
            {
                _logger.LogDebug("✅ Returning cached search results for '{SearchTerm}'", searchTerm);
                return new JsonResult(cachedResult);
            }
        }

        // If filtering by artist, handle external artists
        if (!string.IsNullOrWhiteSpace(artistIds))
        {
            var artistId = artistIds.Split(',')[0]; // Take first artist if multiple
            var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(artistId);
            
            if (isExternal)
            {
                _logger.LogInformation("Fetching albums for external artist: {Provider}/{ExternalId}", provider, externalId);
                return await GetExternalChildItems(provider!, externalId!, includeItemTypes);
            }
        }

        // If no search term, proxy to Jellyfin for browsing
        // If Jellyfin returns empty results, we'll just return empty (not mixing browse with external)
        if (string.IsNullOrWhiteSpace(searchTerm) && string.IsNullOrWhiteSpace(parentId))
        {
            _logger.LogDebug("No search term or parentId, proxying to Jellyfin with full query string");
            
            // Build the full endpoint path with query string
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
                
                _logger.LogInformation("Jellyfin returned {StatusCode}, returning empty result", statusCode);
                return new JsonResult(new { Items = Array.Empty<object>(), TotalRecordCount = 0, StartIndex = startIndex });
            }

            // Update Spotify playlist counts if enabled and response contains playlists
            if (_spotifySettings.Enabled && browseResult.RootElement.TryGetProperty("Items", out var _))
            {
                _logger.LogInformation("Browse result has Items, checking for Spotify playlists to update counts");
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

        // If browsing a specific parent (album, artist, playlist)
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            // Check if this is the music library root - if so, treat as a search
            var isMusicLibrary = parentId == _settings.LibraryId;
            
            if (!isMusicLibrary || string.IsNullOrWhiteSpace(searchTerm))
            {
                _logger.LogDebug("Browsing parent: {ParentId}", parentId);
                return await GetChildItems(parentId, includeItemTypes, limit, startIndex, sortBy);
            }
            
            // If searching within music library root, continue to integrated search below
            _logger.LogInformation("Searching within music library {ParentId}, including external sources", parentId);
        }

        var cleanQuery = searchTerm?.Trim().Trim('"') ?? "";
        _logger.LogInformation("Performing integrated search for: {Query}", cleanQuery);

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

        await Task.WhenAll(jellyfinTask, externalTask, playlistTask);

        var (jellyfinResult, _) = await jellyfinTask;
        var externalResult = await externalTask;
        var playlistResult = await playlistTask;

        _logger.LogInformation("Search results: Jellyfin={JellyfinCount}, External Songs={ExtSongs}, Albums={ExtAlbums}, Artists={ExtArtists}, Playlists={Playlists}",
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
            .Select(s => new { Song = s, Score = FuzzyMatcher.CalculateSimilarity(cleanQuery, s.Title) + (s.IsLocal ? 10.0 : 0.0) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Song)
            .ToList();

        var allAlbums = localAlbums.Concat(externalResult.Albums)
            .Select(a => new { Album = a, Score = FuzzyMatcher.CalculateSimilarity(cleanQuery, a.Title) + (a.IsLocal ? 10.0 : 0.0) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Album)
            .ToList();

        var allArtists = localArtists.Concat(externalResult.Artists)
            .Select(a => new { Artist = a, Score = FuzzyMatcher.CalculateSimilarity(cleanQuery, a.Name) + (a.IsLocal ? 10.0 : 0.0) })
            .OrderByDescending(x => x.Score)
            .Select(x => x.Artist)
            .ToList();

        // Log top results for debugging
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            if (allSongs.Any())
            {
                var topSong = allSongs.First();
                var topScore = FuzzyMatcher.CalculateSimilarity(cleanQuery, topSong.Title) + (topSong.IsLocal ? 10.0 : 0.0);
                _logger.LogDebug("🎵 Top song: '{Title}' (local={IsLocal}, score={Score:F2})", 
                    topSong.Title, topSong.IsLocal, topScore);
            }
            if (allAlbums.Any())
            {
                var topAlbum = allAlbums.First();
                var topScore = FuzzyMatcher.CalculateSimilarity(cleanQuery, topAlbum.Title) + (topAlbum.IsLocal ? 10.0 : 0.0);
                _logger.LogDebug("💿 Top album: '{Title}' (local={IsLocal}, score={Score:F2})", 
                    topAlbum.Title, topAlbum.IsLocal, topScore);
            }
            if (allArtists.Any())
            {
                var topArtist = allArtists.First();
                var topScore = FuzzyMatcher.CalculateSimilarity(cleanQuery, topArtist.Name) + (topArtist.IsLocal ? 10.0 : 0.0);
                _logger.LogDebug("🎤 Top artist: '{Name}' (local={IsLocal}, score={Score:F2})", 
                    topArtist.Name, topArtist.IsLocal, topScore);
            }
        }

        // Convert to Jellyfin format
        var mergedSongs = allSongs.Select(s => _responseBuilder.ConvertSongToJellyfinItem(s)).ToList();
        var mergedAlbums = allAlbums.Select(a => _responseBuilder.ConvertAlbumToJellyfinItem(a)).ToList();
        var mergedArtists = allArtists.Select(a => _responseBuilder.ConvertArtistToJellyfinItem(a)).ToList();

        // Add playlists (preserve their order too)
        if (playlistResult.Count > 0)
        {
            var playlistItems = playlistResult
                .Select(p => _responseBuilder.ConvertPlaylistToJellyfinItem(p))
                .ToList();
            
            mergedAlbums.AddRange(playlistItems);
        }

        _logger.LogInformation("Merged and sorted results by score: Songs={Songs}, Albums={Albums}, Artists={Artists}",
            mergedSongs.Count, mergedAlbums.Count, mergedArtists.Count);

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
                            songItem.TryGetValue("Artists", out var artistsObj) && artistsObj is JsonElement artistsEl &&
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
                    _logger.LogDebug(ex, "Failed to pre-fetch lyrics for search results");
                }
            });
        }

        // Filter by item types if specified
        var items = new List<Dictionary<string, object?>>();

        _logger.LogInformation("Filtering by item types: {ItemTypes}", itemTypes == null ? "null" : string.Join(",", itemTypes));

        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("MusicArtist"))
        {
            _logger.LogInformation("Adding {Count} artists to results", mergedArtists.Count);
            items.AddRange(mergedArtists);
        }
        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("MusicAlbum") || itemTypes.Contains("Playlist"))
        {
            _logger.LogInformation("Adding {Count} albums to results", mergedAlbums.Count);
            items.AddRange(mergedAlbums);
        }
        if (itemTypes == null || itemTypes.Length == 0 || itemTypes.Contains("Audio"))
        {
            _logger.LogInformation("Adding {Count} songs to results", mergedSongs.Count);
            items.AddRange(mergedSongs);
        }

        // Apply pagination
        var pagedItems = items.Skip(startIndex).Take(limit).ToList();

        _logger.LogInformation("Returning {Count} items (total: {Total})", pagedItems.Count, items.Count);

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
            if (!string.IsNullOrWhiteSpace(searchTerm) && string.IsNullOrWhiteSpace(artistIds))
            {
                var cacheKey = $"search:{searchTerm?.ToLowerInvariant()}:{includeItemTypes}:{limit}:{startIndex}";
                await _cache.SetAsync(cacheKey, response, TimeSpan.FromMinutes(15));
                _logger.LogDebug("💾 Cached search results for '{SearchTerm}' (15 min TTL)", searchTerm);
            }

            _logger.LogInformation("About to serialize response...");

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

        // Proxy to Jellyfin for local content
        var (result, statusCode) = await _proxyService.GetItemsAsync(
            parentId: parentId,
            includeItemTypes: ParseItemTypes(includeItemTypes),
            sortBy: sortBy,
            limit: limit,
            startIndex: startIndex,
            clientHeaders: Request.Headers);

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

    #region Items

    /// <summary>
    /// Gets a single item by ID.
    /// </summary>
    [HttpGet("Items/{itemId}")]
    [HttpGet("Users/{userId}/Items/{itemId}")]
    public async Task<IActionResult> GetItem(string itemId, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return _responseBuilder.CreateError(400, "Missing item ID");
        }

        // Check for external playlist
        if (PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            return await GetPlaylistAsAlbum(itemId);
        }

        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(itemId);

        if (isExternal)
        {
            return await GetExternalItem(provider!, type, externalId!);
        }

        // Proxy to Jellyfin
        var (result, statusCode) = await _proxyService.GetItemAsync(itemId, Request.Headers);
        
        return HandleProxyResponse(result, statusCode);
    }

    /// <summary>
    /// Gets an external item (song, album, or artist).
    /// </summary>
    private async Task<IActionResult> GetExternalItem(string provider, string? type, string externalId)
    {
        switch (type)
        {
            case "song":
                var song = await _metadataService.GetSongAsync(provider, externalId);
                if (song == null) return _responseBuilder.CreateError(404, "Song not found");
                return _responseBuilder.CreateSongResponse(song);

            case "album":
                var album = await _metadataService.GetAlbumAsync(provider, externalId);
                if (album == null) return _responseBuilder.CreateError(404, "Album not found");
                return _responseBuilder.CreateAlbumResponse(album);

            case "artist":
                var artist = await _metadataService.GetArtistAsync(provider, externalId);
                if (artist == null) return _responseBuilder.CreateError(404, "Artist not found");
                var albums = await _metadataService.GetArtistAlbumsAsync(provider, externalId);
                
                // Fill in artist info for albums
                foreach (var a in albums)
                {
                    if (string.IsNullOrEmpty(a.Artist)) a.Artist = artist.Name;
                    if (string.IsNullOrEmpty(a.ArtistId)) a.ArtistId = artist.Id;
                }
                
                return _responseBuilder.CreateArtistResponse(artist, albums);

            default:
                // Try song first, then album
                var s = await _metadataService.GetSongAsync(provider, externalId);
                if (s != null) return _responseBuilder.CreateSongResponse(s);

                var alb = await _metadataService.GetAlbumAsync(provider, externalId);
                if (alb != null) return _responseBuilder.CreateAlbumResponse(alb);

                return _responseBuilder.CreateError(404, "Item not found");
        }
    }

    /// <summary>
    /// Gets child items for an external parent (album tracks or artist albums).
    /// </summary>
    private async Task<IActionResult> GetExternalChildItems(string provider, string externalId, string? includeItemTypes)
    {
        var itemTypes = ParseItemTypes(includeItemTypes);

        _logger.LogInformation("GetExternalChildItems: provider={Provider}, externalId={ExternalId}, itemTypes={ItemTypes}", 
            provider, externalId, string.Join(",", itemTypes ?? Array.Empty<string>()));

        // Check if asking for audio (album tracks)
        if (itemTypes?.Contains("Audio") == true)
        {
            _logger.LogDebug("Fetching album tracks for {Provider}/{ExternalId}", provider, externalId);
            var album = await _metadataService.GetAlbumAsync(provider, externalId);
            if (album == null)
            {
                return _responseBuilder.CreateError(404, "Album not found");
            }

            return _responseBuilder.CreateItemsResponse(album.Songs);
        }

        // Otherwise assume it's artist albums
        _logger.LogDebug("Fetching artist albums for {Provider}/{ExternalId}", provider, externalId);
        var albums = await _metadataService.GetArtistAlbumsAsync(provider, externalId);
        var artist = await _metadataService.GetArtistAsync(provider, externalId);

        _logger.LogInformation("Found {Count} albums for artist {ArtistName}", albums.Count, artist?.Name ?? "unknown");

        // Fill artist info
        if (artist != null)
        {
            foreach (var a in albums)
            {
                if (string.IsNullOrEmpty(a.Artist)) a.Artist = artist.Name;
                if (string.IsNullOrEmpty(a.ArtistId)) a.ArtistId = artist.Id;
            }
        }

        return _responseBuilder.CreateAlbumsResponse(albums);
    }

    #endregion

    #region Artists

    /// <summary>
    /// Gets artists from the library.
    /// Supports both /Artists and /Artists/AlbumArtists routes.
    /// When searchTerm is provided, integrates external search results.
    /// </summary>
    [HttpGet("Artists", Order = 1)]
    [HttpGet("Artists/AlbumArtists", Order = 1)]
    public async Task<IActionResult> GetArtists(
        [FromQuery] string? searchTerm,
        [FromQuery] int limit = 50,
        [FromQuery] int startIndex = 0)
    {
        _logger.LogInformation("GetArtists called: searchTerm={SearchTerm}, limit={Limit}", searchTerm, limit);

        // If there's a search term, integrate external results
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var cleanQuery = searchTerm.Trim().Trim('"');
            _logger.LogInformation("Searching artists for: {Query}", cleanQuery);

            // Run local and external searches in parallel
            var jellyfinTask = _proxyService.GetArtistsAsync(searchTerm, limit, startIndex, Request.Headers);
            var externalTask = _metadataService.SearchArtistsAsync(cleanQuery, limit);

            await Task.WhenAll(jellyfinTask, externalTask);

            var (jellyfinResult, _) = await jellyfinTask;
            var externalArtists = await externalTask;

            _logger.LogInformation("Artist search results: Jellyfin={JellyfinCount}, External={ExternalCount}",
                jellyfinResult != null ? "found" : "null", externalArtists.Count);

            // Parse Jellyfin artists
            var localArtists = new List<Artist>();
            if (jellyfinResult != null && jellyfinResult.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    localArtists.Add(_modelMapper.ParseArtist(item));
                }
            }

            // NO deduplication - merge all artists and sort by relevance
            // Show ALL matches (local + external) sorted by best match first
            var mergedArtists = localArtists.Concat(externalArtists).ToList();

            _logger.LogInformation("Returning {Count} total artists (local + external, no deduplication)", mergedArtists.Count);

            // Convert to Jellyfin format
            var artistItems = mergedArtists.Select(a => _responseBuilder.ConvertArtistToJellyfinItem(a)).ToList();

            return _responseBuilder.CreateJsonResponse(new
            {
                Items = artistItems,
                TotalRecordCount = artistItems.Count,
                StartIndex = startIndex
            });
        }

        // No search term - just proxy to Jellyfin
        var (result, statusCode) = await _proxyService.GetArtistsAsync(searchTerm, limit, startIndex, Request.Headers);

        return HandleProxyResponse(result, statusCode, new
        {
            Items = Array.Empty<object>(),
            TotalRecordCount = 0,
            StartIndex = startIndex
        });
    }

    /// <summary>
    /// Gets a single artist by ID or name.
    /// This route has lower priority to avoid conflicting with Artists/AlbumArtists.
    /// </summary>
    [HttpGet("Artists/{artistIdOrName}", Order = 10)]
    public async Task<IActionResult> GetArtist(string artistIdOrName)
    {
        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(artistIdOrName);

        if (isExternal)
        {
            var artist = await _metadataService.GetArtistAsync(provider!, externalId!);
            if (artist == null)
            {
                return _responseBuilder.CreateError(404, "Artist not found");
            }

            var albums = await _metadataService.GetArtistAlbumsAsync(provider!, externalId!);
            foreach (var a in albums)
            {
                if (string.IsNullOrEmpty(a.Artist)) a.Artist = artist.Name;
                if (string.IsNullOrEmpty(a.ArtistId)) a.ArtistId = artist.Id;
            }

            return _responseBuilder.CreateArtistResponse(artist, albums);
        }

        // Get local artist from Jellyfin
        var (jellyfinArtist, statusCode) = await _proxyService.GetArtistAsync(artistIdOrName, Request.Headers);
        if (jellyfinArtist == null)
        {
            return HandleProxyResponse(null, statusCode);
        }

        var artistData = _modelMapper.ParseArtist(jellyfinArtist.RootElement);
        var artistName = artistData.Name;
        var localArtistId = artistData.Id;

        // Get local albums
        var (localAlbumsResult, _) = await _proxyService.GetItemsAsync(
            parentId: null,
            includeItemTypes: new[] { "MusicAlbum" },
            sortBy: "SortName",
            clientHeaders: Request.Headers);

        var (_, localAlbums, _) = _modelMapper.ParseItemsResponse(localAlbumsResult);

        // Filter to just this artist's albums
        var artistAlbums = localAlbums
            .Where(a => a.ArtistId == localArtistId || 
                       (a.Artist?.Equals(artistName, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        // Search for external albums by this artist
        var externalArtists = await _metadataService.SearchArtistsAsync(artistName, 1);
        var externalAlbums = new List<Album>();

        if (externalArtists.Count > 0)
        {
            var extArtist = externalArtists[0];
            if (extArtist.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            {
                externalAlbums = await _metadataService.GetArtistAlbumsAsync("deezer", extArtist.ExternalId!);

                // Set artist info to local artist so albums link back correctly
                foreach (var a in externalAlbums)
                {
                    if (string.IsNullOrEmpty(a.Artist)) a.Artist = artistName;
                    if (string.IsNullOrEmpty(a.ArtistId)) a.ArtistId = localArtistId;
                }
            }
        }

        // Deduplicate albums by title
        var localAlbumTitles = new HashSet<string>(artistAlbums.Select(a => a.Title), StringComparer.OrdinalIgnoreCase);
        var mergedAlbums = artistAlbums.ToList();
        mergedAlbums.AddRange(externalAlbums.Where(a => !localAlbumTitles.Contains(a.Title)));

        return _responseBuilder.CreateArtistResponse(artistData, mergedAlbums);
    }

    #endregion

    #region Audio Streaming

    /// <summary>
    /// Downloads/streams audio. Works with local and external content.
    /// </summary>
    [HttpGet("Items/{itemId}/Download")]
    [HttpGet("Items/{itemId}/File")]
    public async Task<IActionResult> DownloadAudio(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
            // Build path for Jellyfin download/file endpoint
            var endpoint = Request.Path.Value?.Contains("/File", StringComparison.OrdinalIgnoreCase) == true ? "File" : "Download";
            var fullPath = $"Items/{itemId}/{endpoint}";
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        // Handle external content
        return await StreamExternalContent(provider!, externalId!);
    }

    /// <summary>
    /// Streams audio for a given item. Downloads on-demand for external content.
    /// </summary>
    [HttpGet("Audio/{itemId}/stream")]
    [HttpGet("Audio/{itemId}/stream.{container}")]
    public async Task<IActionResult> StreamAudio(string itemId, string? container = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
            // Build path for Jellyfin stream
            var fullPath = string.IsNullOrEmpty(container) 
                ? $"Audio/{itemId}/stream" 
                : $"Audio/{itemId}/stream.{container}";
            
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        // Handle external content
        return await StreamExternalContent(provider!, externalId!);
    }

    /// <summary>
    /// Proxies a stream from Jellyfin with proper header forwarding.
    /// </summary>
    private async Task<IActionResult> ProxyJellyfinStream(string path, string itemId)
    {
        var jellyfinUrl = $"{_settings.Url?.TrimEnd('/')}/{path}";
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, jellyfinUrl);
            
            // Forward auth headers
            if (Request.Headers.TryGetValue("X-Emby-Authorization", out var embyAuth))
            {
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", embyAuth.ToString());
            }
            else if (Request.Headers.TryGetValue("Authorization", out var auth))
            {
                request.Headers.TryAddWithoutValidation("Authorization", auth.ToString());
            }
            
            // Forward Range header for seeking
            if (Request.Headers.TryGetValue("Range", out var range))
            {
                request.Headers.TryAddWithoutValidation("Range", range.ToString());
            }
            
            var response = await _proxyService.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Jellyfin stream failed: {StatusCode} for {ItemId}", response.StatusCode, itemId);
                return StatusCode((int)response.StatusCode);
            }
            
            // Set response status and headers
            Response.StatusCode = (int)response.StatusCode;
            
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
            
            // Forward caching headers for client-side caching
            if (response.Headers.ETag != null)
            {
                Response.Headers["ETag"] = response.Headers.ETag.ToString();
            }
            
            if (response.Content.Headers.LastModified.HasValue)
            {
                Response.Headers["Last-Modified"] = response.Content.Headers.LastModified.Value.ToString("R");
            }
            
            if (response.Headers.CacheControl != null)
            {
                Response.Headers["Cache-Control"] = response.Headers.CacheControl.ToString();
            }
            
            // Forward range headers for seeking
            if (response.Content.Headers.ContentRange != null)
            {
                Response.Headers["Content-Range"] = response.Content.Headers.ContentRange.ToString();
            }
            
            if (response.Headers.AcceptRanges != null)
            {
                Response.Headers["Accept-Ranges"] = string.Join(", ", response.Headers.AcceptRanges);
            }
            
            if (response.Content.Headers.ContentLength.HasValue)
            {
                Response.Headers["Content-Length"] = response.Content.Headers.ContentLength.Value.ToString();
            }
            
            var stream = await response.Content.ReadAsStreamAsync();
            return File(stream, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy stream from Jellyfin for {ItemId}", itemId);
            return StatusCode(500, new { error = $"Streaming failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Streams external content, using cache if available or downloading on-demand.
    /// </summary>
    private async Task<IActionResult> StreamExternalContent(string provider, string externalId)
    {
        // Check for locally cached file
        var localPath = await _localLibraryService.GetLocalPathForExternalSongAsync(provider, externalId);

        if (localPath != null && System.IO.File.Exists(localPath))
        {
            // Update last access time for cache cleanup
            try
            {
                System.IO.File.SetLastAccessTimeUtc(localPath, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update last access time for {Path}", localPath);
            }
            
            var stream = System.IO.File.OpenRead(localPath);
            return File(stream, GetContentType(localPath), enableRangeProcessing: true);
        }

        // Download and stream on-demand
        try
        {
            var downloadStream = await _downloadService.DownloadAndStreamAsync(
                provider, 
                externalId, 
                HttpContext.RequestAborted);

            return File(downloadStream, "audio/mpeg", enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stream external song {Provider}:{ExternalId}", provider, externalId);
            return StatusCode(500, new { error = $"Streaming failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Universal audio endpoint - handles transcoding, format negotiation, and adaptive streaming.
    /// This is the primary endpoint used by Jellyfin Web and most clients.
    /// </summary>
    [HttpGet("Audio/{itemId}/universal")]
    [HttpHead("Audio/{itemId}/universal")]
    public async Task<IActionResult> UniversalAudio(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return BadRequest(new { error = "Missing item ID" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        if (!isExternal)
        {
            // For local content, proxy the universal endpoint with all query parameters
            var fullPath = $"Audio/{itemId}/universal";
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{fullPath}{Request.QueryString.Value}";
            }

            return await ProxyJellyfinStream(fullPath, itemId);
        }

        // For external content, use simple streaming (no transcoding support yet)
        return await StreamExternalContent(provider!, externalId!);
    }

    #endregion

    #region Images

    /// <summary>
    /// Gets the primary image for an item.
    /// </summary>
    [HttpGet("Items/{itemId}/Images/{imageType}")]
    [HttpGet("Items/{itemId}/Images/{imageType}/{imageIndex}")]
    public async Task<IActionResult> GetImage(
        string itemId,
        string imageType,
        int imageIndex = 0,
        [FromQuery] int? maxWidth = null,
        [FromQuery] int? maxHeight = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return NotFound();
        }

        // Check for external playlist
        if (PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            return await GetPlaylistImage(itemId);
        }

        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(itemId);

        if (!isExternal)
        {
            // Proxy image from Jellyfin for local content
            var (imageBytes, contentType) = await _proxyService.GetImageAsync(
                itemId,
                imageType,
                maxWidth,
                maxHeight);
            
            if (imageBytes == null || contentType == null)
            {
                // Return placeholder if Jellyfin doesn't have image
                return await GetPlaceholderImageAsync();
            }
            
            return File(imageBytes, contentType);
        }

        // Get external cover art URL
        string? coverUrl = type switch
        {
            "artist" => (await _metadataService.GetArtistAsync(provider!, externalId!))?.ImageUrl,
            "album" => (await _metadataService.GetAlbumAsync(provider!, externalId!))?.CoverArtUrl,
            "song" => (await _metadataService.GetSongAsync(provider!, externalId!))?.CoverArtUrl,
            _ => null
        };

        if (string.IsNullOrEmpty(coverUrl))
        {
            // Return placeholder "no image available" image
            return await GetPlaceholderImageAsync();
        }

        // Fetch and return the image using the proxy service's HttpClient
        try
        {
            var response = await _proxyService.HttpClient.GetAsync(coverUrl);
            if (!response.IsSuccessStatusCode)
            {
                // Return placeholder on fetch failure
                return await GetPlaceholderImageAsync();
            }

            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            return File(imageBytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch cover art from {Url}", coverUrl);
            // Return placeholder on exception
            return await GetPlaceholderImageAsync();
        }
    }

    /// <summary>
    /// Returns a placeholder "no image available" image.
    /// Generates a simple 1x1 transparent PNG as a minimal placeholder.
    /// TODO: Replace with actual "no image available" graphic from wwwroot/placeholder.png
    /// </summary>
    private async Task<IActionResult> GetPlaceholderImageAsync()
    {
        // Check if custom placeholder exists in wwwroot
        var placeholderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "placeholder.png");
        if (System.IO.File.Exists(placeholderPath))
        {
            var imageBytes = await System.IO.File.ReadAllBytesAsync(placeholderPath);
            return File(imageBytes, "image/png");
        }
        
        // Fallback: Return a 1x1 transparent PNG as minimal placeholder
        var transparentPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
        );
        
        return File(transparentPng, "image/png");
    }

    #endregion

    #region Lyrics

    /// <summary>
    /// Gets lyrics for an item.
    /// Priority: 1. Jellyfin embedded lyrics, 2. Spotify synced lyrics, 3. LRCLIB
    /// </summary>
    [HttpGet("Audio/{itemId}/Lyrics")]
    [HttpGet("Items/{itemId}/Lyrics")]
    public async Task<IActionResult> GetLyrics(string itemId)
    {
        _logger.LogInformation("🎵 GetLyrics called for itemId: {ItemId}", itemId);
        
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return NotFound();
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);
        
        _logger.LogInformation("🎵 Lyrics request: itemId={ItemId}, isExternal={IsExternal}, provider={Provider}, externalId={ExternalId}", 
            itemId, isExternal, provider, externalId);

        // For local tracks, check if Jellyfin already has embedded lyrics
        if (!isExternal)
        {
            _logger.LogInformation("Checking Jellyfin for embedded lyrics for local track: {ItemId}", itemId);
            
            // Try to get lyrics from Jellyfin first (it reads embedded lyrics from files)
            var (jellyfinLyrics, statusCode) = await _proxyService.GetJsonAsync($"Audio/{itemId}/Lyrics", null, Request.Headers);
            
            _logger.LogInformation("Jellyfin lyrics check result: statusCode={StatusCode}, hasLyrics={HasLyrics}", 
                statusCode, jellyfinLyrics != null);
            
            if (jellyfinLyrics != null && statusCode == 200)
            {
                _logger.LogInformation("Found embedded lyrics in Jellyfin for track {ItemId}", itemId);
                return new JsonResult(JsonSerializer.Deserialize<object>(jellyfinLyrics.RootElement.GetRawText()));
            }
            
            _logger.LogInformation("No embedded lyrics found in Jellyfin (status: {StatusCode}), trying Spotify/LRCLIB", statusCode);
        }

        // Get song metadata for lyrics search
        Song? song = null;
        string? spotifyTrackId = null;
        
        if (isExternal)
        {
            song = await _metadataService.GetSongAsync(provider!, externalId!);
            
            // Use Spotify ID from song metadata if available (populated during GetSongAsync)
            if (song != null && !string.IsNullOrEmpty(song.SpotifyId))
            {
                spotifyTrackId = song.SpotifyId;
                _logger.LogInformation("Using Spotify ID {SpotifyId} from song metadata for {Provider}/{ExternalId}", 
                    spotifyTrackId, provider, externalId);
            }
            // Fallback: Try to find Spotify ID from matched tracks cache
            else if (song != null)
            {
                spotifyTrackId = await FindSpotifyIdForExternalTrackAsync(song);
                if (!string.IsNullOrEmpty(spotifyTrackId))
                {
                    _logger.LogInformation("Found Spotify ID {SpotifyId} for external track {Provider}/{ExternalId} from cache", 
                        spotifyTrackId, provider, externalId);
                }
                else
                {
                    // Last resort: Try to convert via Odesli/song.link
                    if (provider == "squidwtf")
                    {
                        spotifyTrackId = await _odesliService.ConvertTidalToSpotifyIdAsync(externalId!, HttpContext.RequestAborted);
                    }
                    else
                    {
                        // For other providers, build the URL and convert
                        var sourceUrl = provider?.ToLowerInvariant() switch
                        {
                            "deezer" => $"https://www.deezer.com/track/{externalId}",
                            "qobuz" => $"https://www.qobuz.com/us-en/album/-/-/{externalId}",
                            _ => null
                        };
                        
                        if (!string.IsNullOrEmpty(sourceUrl))
                        {
                            spotifyTrackId = await _odesliService.ConvertUrlToSpotifyIdAsync(sourceUrl, HttpContext.RequestAborted);
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(spotifyTrackId))
                    {
                        _logger.LogInformation("Converted {Provider}/{ExternalId} to Spotify ID {SpotifyId} via Odesli", 
                            provider, externalId, spotifyTrackId);
                    }
                }
            }
        }
        else
        {
            // For local songs, get metadata from Jellyfin
            var (item, _) = await _proxyService.GetItemAsync(itemId, Request.Headers);
            if (item != null && item.RootElement.TryGetProperty("Type", out var typeEl) && 
                typeEl.GetString() == "Audio")
            {
                song = new Song
                {
                    Title = item.RootElement.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                    Artist = item.RootElement.TryGetProperty("AlbumArtist", out var artist) ? artist.GetString() ?? "" : "",
                    Album = item.RootElement.TryGetProperty("Album", out var album) ? album.GetString() ?? "" : "",
                    Duration = item.RootElement.TryGetProperty("RunTimeTicks", out var ticks) ? (int)(ticks.GetInt64() / 10000000) : 0
                };
                
                // Check for Spotify ID in provider IDs
                if (item.RootElement.TryGetProperty("ProviderIds", out var providerIds))
                {
                    if (providerIds.TryGetProperty("Spotify", out var spotifyId))
                    {
                        spotifyTrackId = spotifyId.GetString();
                    }
                }
            }
        }

        if (song == null)
        {
            return NotFound(new { error = "Song not found" });
        }

        // Strip [S] suffix from title, artist, and album for lyrics search
        // The [S] tag is added to external tracks but shouldn't be used in lyrics queries
        var searchTitle = song.Title.Replace(" [S]", "").Trim();
        var searchArtist = song.Artist?.Replace(" [S]", "").Trim() ?? "";
        var searchAlbum = song.Album?.Replace(" [S]", "").Trim() ?? "";
        var searchArtists = song.Artists.Select(a => a.Replace(" [S]", "").Trim()).ToList();
        
        if (searchArtists.Count == 0 && !string.IsNullOrEmpty(searchArtist))
        {
            searchArtists.Add(searchArtist);
        }

        LyricsInfo? lyrics = null;
        
        // Try Spotify lyrics ONLY if we have a valid Spotify track ID
        // Spotify lyrics only work for tracks from injected playlists that have been matched
        if (_spotifyLyricsService != null && _spotifyApiSettings.Enabled && !string.IsNullOrEmpty(spotifyTrackId))
        {
            // Validate that this is a real Spotify ID (not spotify:local or other invalid formats)
            var cleanSpotifyId = spotifyTrackId.Replace("spotify:track:", "").Trim();
            
            // Spotify track IDs are 22 characters, base62 encoded
            if (cleanSpotifyId.Length == 22 && !cleanSpotifyId.Contains(":") && !cleanSpotifyId.Contains("local"))
            {
                _logger.LogInformation("Trying Spotify lyrics for track ID: {SpotifyId} ({Artist} - {Title})", 
                    cleanSpotifyId, searchArtist, searchTitle);
                
                var spotifyLyrics = await _spotifyLyricsService.GetLyricsByTrackIdAsync(cleanSpotifyId);
                
                if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
                {
                    _logger.LogInformation("Found Spotify lyrics for {Artist} - {Title} ({LineCount} lines, type: {SyncType})", 
                        searchArtist, searchTitle, spotifyLyrics.Lines.Count, spotifyLyrics.SyncType);
                    lyrics = _spotifyLyricsService.ToLyricsInfo(spotifyLyrics);
                }
                else
                {
                    _logger.LogDebug("No Spotify lyrics found for track ID {SpotifyId}", cleanSpotifyId);
                }
            }
            else
            {
                _logger.LogDebug("Invalid Spotify ID format: {SpotifyId}, skipping Spotify lyrics", spotifyTrackId);
            }
        }
        
        // Fall back to LRCLIB if no Spotify lyrics
        if (lyrics == null)
        {
            _logger.LogInformation("Searching LRCLIB for lyrics: {Artists} - {Title}", 
                string.Join(", ", searchArtists), 
                searchTitle);
            var lrclibService = HttpContext.RequestServices.GetService<LrclibService>();
            if (lrclibService != null)
            {
                lyrics = await lrclibService.GetLyricsAsync(
                    searchTitle,
                    searchArtists.ToArray(),
                    searchAlbum,
                    song.Duration ?? 0);
            }
        }

        if (lyrics == null)
        {
            return NotFound(new { error = "Lyrics not found" });
        }

        // Prefer synced lyrics, fall back to plain
        var lyricsText = lyrics.SyncedLyrics ?? lyrics.PlainLyrics ?? "";
        var isSynced = !string.IsNullOrEmpty(lyrics.SyncedLyrics);

        _logger.LogInformation("Lyrics for {Artist} - {Track}: synced={HasSynced}, plainLength={PlainLen}, syncedLength={SyncLen}", 
            song.Artist, song.Title, isSynced, lyrics.PlainLyrics?.Length ?? 0, lyrics.SyncedLyrics?.Length ?? 0);

        // Parse LRC format into individual lines for Jellyfin
        var lyricLines = new List<Dictionary<string, object>>();
        
        if (isSynced && !string.IsNullOrEmpty(lyrics.SyncedLyrics))
        {
            _logger.LogInformation("Parsing synced lyrics (LRC format)");
            // Parse LRC format: [mm:ss.xx] text
            // Skip ID tags like [ar:Artist], [ti:Title], etc.
            var lines = lyrics.SyncedLyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // Match timestamp format [mm:ss.xx] or [mm:ss.xxx]
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^\[(\d+):(\d+)\.(\d+)\]\s*(.*)$");
                if (match.Success)
                {
                    var minutes = int.Parse(match.Groups[1].Value);
                    var seconds = int.Parse(match.Groups[2].Value);
                    var centiseconds = int.Parse(match.Groups[3].Value);
                    var text = match.Groups[4].Value;
                    
                    // Convert to ticks (100 nanoseconds)
                    var totalMilliseconds = (minutes * 60 + seconds) * 1000 + centiseconds * 10;
                    var ticks = totalMilliseconds * 10000L;
                    
                    // For synced lyrics, include Start timestamp
                    lyricLines.Add(new Dictionary<string, object>
                    {
                        ["Text"] = text,
                        ["Start"] = ticks
                    });
                }
                // Skip ID tags like [ar:Artist], [ti:Title], [length:2:23], etc.
            }
            _logger.LogInformation("Parsed {Count} synced lyric lines (skipped ID tags)", lyricLines.Count);
        }
        else if (!string.IsNullOrEmpty(lyricsText))
        {
            _logger.LogInformation("Splitting plain lyrics into lines (no timestamps)");
            // Plain lyrics - split by newlines and return each line separately
            // IMPORTANT: Do NOT include "Start" field at all for unsynced lyrics
            // Including it (even as null) causes clients to treat it as synced with timestamp 0:00
            var lines = lyricsText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                lyricLines.Add(new Dictionary<string, object>
                {
                    ["Text"] = line.Trim()
                });
            }
            _logger.LogInformation("Split into {Count} plain lyric lines", lyricLines.Count);
        }
        else
        {
            _logger.LogWarning("No lyrics text available");
            // No lyrics at all
            lyricLines.Add(new Dictionary<string, object>
            {
                ["Text"] = ""
            });
        }

        var response = new
        {
            Metadata = new
            {
                Artist = lyrics.ArtistName,
                Album = lyrics.AlbumName,
                Title = lyrics.TrackName,
                Length = lyrics.Duration,
                IsSynced = isSynced
            },
            Lyrics = lyricLines
        };

        _logger.LogInformation("Returning lyrics response: {LineCount} lines, synced={IsSynced}", lyricLines.Count, isSynced);
        
        // Log a sample of the response for debugging
        if (lyricLines.Count > 0)
        {
            var sampleLine = lyricLines[0];
            var hasStart = sampleLine.ContainsKey("Start");
            _logger.LogInformation("Sample line: Text='{Text}', HasStart={HasStart}", 
                sampleLine.GetValueOrDefault("Text"), hasStart);
        }

        return Ok(response);
    }
    
    /// <summary>
    /// Proactively fetches and caches lyrics for a track in the background.
    /// Called when playback starts to ensure lyrics are ready when requested.
    /// </summary>
    private async Task PrefetchLyricsForTrackAsync(string itemId, bool isExternal, string? provider, string? externalId)
    {
        try
        {
            Song? song = null;
            string? spotifyTrackId = null;
            
            if (isExternal && !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(externalId))
            {
                // Get external track metadata
                song = await _metadataService.GetSongAsync(provider, externalId);
                
                // Try to find Spotify ID from matched tracks cache
                if (song != null)
                {
                    spotifyTrackId = await FindSpotifyIdForExternalTrackAsync(song);
                    
                    // If no cached Spotify ID, try Odesli conversion
                    if (string.IsNullOrEmpty(spotifyTrackId) && provider == "squidwtf")
                    {
                        spotifyTrackId = await _odesliService.ConvertTidalToSpotifyIdAsync(externalId, HttpContext.RequestAborted);
                    }
                }
            }
            else
            {
                // Get local track metadata from Jellyfin
                var (item, _) = await _proxyService.GetItemAsync(itemId, Request.Headers);
                if (item != null && item.RootElement.TryGetProperty("Type", out var typeEl) && 
                    typeEl.GetString() == "Audio")
                {
                    song = new Song
                    {
                        Title = item.RootElement.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                        Artist = item.RootElement.TryGetProperty("AlbumArtist", out var artist) ? artist.GetString() ?? "" : "",
                        Album = item.RootElement.TryGetProperty("Album", out var album) ? album.GetString() ?? "" : "",
                        Duration = item.RootElement.TryGetProperty("RunTimeTicks", out var ticks) ? (int)(ticks.GetInt64() / 10000000) : 0
                    };
                    
                    // Check for Spotify ID in provider IDs
                    if (item.RootElement.TryGetProperty("ProviderIds", out var providerIds))
                    {
                        if (providerIds.TryGetProperty("Spotify", out var spotifyId))
                        {
                            spotifyTrackId = spotifyId.GetString();
                        }
                    }
                }
            }
            
            if (song == null)
            {
                _logger.LogDebug("Could not get song metadata for lyrics prefetch: {ItemId}", itemId);
                return;
            }
            
            // Strip [S] suffix for lyrics search
            var searchTitle = song.Title.Replace(" [S]", "").Trim();
            var searchArtist = song.Artist?.Replace(" [S]", "").Trim() ?? "";
            var searchAlbum = song.Album?.Replace(" [S]", "").Trim() ?? "";
            var searchArtists = song.Artists.Select(a => a.Replace(" [S]", "").Trim()).ToList();
            
            if (searchArtists.Count == 0 && !string.IsNullOrEmpty(searchArtist))
            {
                searchArtists.Add(searchArtist);
            }
            
            _logger.LogDebug("🎵 Prefetching lyrics for: {Artist} - {Title}", searchArtist, searchTitle);
            
            // Try Spotify lyrics if we have a valid Spotify track ID
            if (_spotifyLyricsService != null && _spotifyApiSettings.Enabled && !string.IsNullOrEmpty(spotifyTrackId))
            {
                var cleanSpotifyId = spotifyTrackId.Replace("spotify:track:", "").Trim();
                
                if (cleanSpotifyId.Length == 22 && !cleanSpotifyId.Contains(":") && !cleanSpotifyId.Contains("local"))
                {
                    var spotifyLyrics = await _spotifyLyricsService.GetLyricsByTrackIdAsync(cleanSpotifyId);
                    
                    if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
                    {
                        _logger.LogDebug("✓ Prefetched Spotify lyrics for {Artist} - {Title} ({LineCount} lines)", 
                            searchArtist, searchTitle, spotifyLyrics.Lines.Count);
                        return; // Success, lyrics are now cached
                    }
                }
            }
            
            // Fall back to LRCLIB
            if (_lrclibService != null)
            {
                var lyrics = await _lrclibService.GetLyricsAsync(
                    searchTitle,
                    searchArtists.ToArray(),
                    searchAlbum,
                    song.Duration ?? 0);
                
                if (lyrics != null)
                {
                    _logger.LogDebug("✓ Prefetched LRCLIB lyrics for {Artist} - {Title}", searchArtist, searchTitle);
                }
                else
                {
                    _logger.LogDebug("No lyrics found for {Artist} - {Title}", searchArtist, searchTitle);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error prefetching lyrics for track {ItemId}", itemId);
        }
    }

    #endregion

    #region Favorites

    /// <summary>
    /// Marks an item as favorite. For playlists, triggers a full download.
    /// Supports both /Users/{userId}/FavoriteItems/{itemId} and /UserFavoriteItems/{itemId}?userId=xxx
    /// </summary>
    [HttpPost("Users/{userId}/FavoriteItems/{itemId}")]
    [HttpPost("UserFavoriteItems/{itemId}")]
    public async Task<IActionResult> MarkFavorite(string itemId, string? userId = null)
    {
        // Get userId from query string if not in path
        if (string.IsNullOrEmpty(userId))
        {
            userId = Request.Query["userId"].ToString();
        }
        
        _logger.LogInformation("MarkFavorite called: userId={UserId}, itemId={ItemId}, route={Route}", 
            userId, itemId, Request.Path);
        
        // Check if this is an external playlist - trigger download
        if (PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            if (_playlistSyncService == null)
            {
                return _responseBuilder.CreateError(500, "Playlist functionality not enabled");
            }

            _logger.LogInformation("Favoriting external playlist {PlaylistId}, triggering download", itemId);

            // Start download in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await _playlistSyncService.DownloadFullPlaylistAsync(itemId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download playlist {PlaylistId}", itemId);
                }
            });

            // Return a minimal UserItemDataDto response
            return Ok(new 
            { 
                IsFavorite = true,
                ItemId = itemId
            });
        }

        // Check if this is an external song/album
        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);
        if (isExternal)
        {
            _logger.LogInformation("Favoriting external item {ItemId}, copying to kept folder", itemId);
            
            // Copy the track to kept folder in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await CopyExternalTrackToKeptAsync(itemId, provider!, externalId!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to copy external track {ItemId} to kept folder", itemId);
                }
            });
            
            // Return a minimal UserItemDataDto response
            return Ok(new 
            { 
                IsFavorite = true,
                ItemId = itemId
            });
        }

        // For local Jellyfin items, proxy the request through
        // Use the official Jellyfin endpoint format
        var endpoint = $"UserFavoriteItems/{itemId}";
        if (!string.IsNullOrEmpty(userId))
        {
            endpoint = $"{endpoint}?userId={userId}";
        }
        
        _logger.LogInformation("Proxying favorite request to Jellyfin: {Endpoint}", endpoint);
        
        var (result, statusCode) = await _proxyService.PostJsonAsync(endpoint, "{}", Request.Headers);
        
        return HandleProxyResponse(result, statusCode);
    }

    /// <summary>
    /// Removes an item from favorites.
    /// Supports both /Users/{userId}/FavoriteItems/{itemId} and /UserFavoriteItems/{itemId}?userId=xxx
    /// </summary>
    [HttpDelete("Users/{userId}/FavoriteItems/{itemId}")]
    [HttpDelete("UserFavoriteItems/{itemId}")]
    public async Task<IActionResult> UnmarkFavorite(string itemId, string? userId = null)
    {
        // Get userId from query string if not in path
        if (string.IsNullOrEmpty(userId))
        {
            userId = Request.Query["userId"].ToString();
        }
        
        _logger.LogInformation("UnmarkFavorite called: userId={UserId}, itemId={ItemId}, route={Route}", 
            userId, itemId, Request.Path);
        
        // External items - remove from kept folder if it exists
        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);
        if (isExternal || PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            _logger.LogInformation("Unfavoriting external item {ItemId} - removing from kept folder", itemId);
            
            // Remove from kept folder in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await RemoveExternalTrackFromKeptAsync(itemId, provider!, externalId!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove external track {ItemId} from kept folder", itemId);
                }
            });
            
            return Ok(new 
            { 
                IsFavorite = false,
                ItemId = itemId
            });
        }

        // Proxy to Jellyfin to unfavorite
        // Use the official Jellyfin endpoint format
        var endpoint = $"UserFavoriteItems/{itemId}";
        if (!string.IsNullOrEmpty(userId))
        {
            endpoint = $"{endpoint}?userId={userId}";
        }
        
        _logger.LogInformation("Proxying unfavorite request to Jellyfin: {Endpoint}", endpoint);
        
        var (result, statusCode) = await _proxyService.DeleteAsync(endpoint, Request.Headers);
        
        return HandleProxyResponse(result, statusCode, new 
        { 
            IsFavorite = false,
            ItemId = itemId
        });
    }

    #endregion

    #region Playlists

    /// <summary>
    /// Gets playlist tracks displayed as an album.
    /// </summary>
    private async Task<IActionResult> GetPlaylistAsAlbum(string playlistId)
    {
        try
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);

            var playlist = await _metadataService.GetPlaylistAsync(provider, externalId);
            if (playlist == null)
            {
                return _responseBuilder.CreateError(404, "Playlist not found");
            }

            var tracks = await _metadataService.GetPlaylistTracksAsync(provider, externalId);

            // Cache tracks for playlist sync
            if (_playlistSyncService != null)
            {
                foreach (var track in tracks)
                {
                    if (!string.IsNullOrEmpty(track.ExternalId))
                    {
                        var trackId = $"ext-{provider}-{track.ExternalId}";
                        _playlistSyncService.AddTrackToPlaylistCache(trackId, playlistId);
                    }
                }
                _logger.LogDebug("Cached {Count} tracks for playlist {PlaylistId}", tracks.Count, playlistId);
            }

            return _responseBuilder.CreatePlaylistAsAlbumResponse(playlist, tracks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting playlist {PlaylistId}", playlistId);
            return _responseBuilder.CreateError(500, "Failed to get playlist");
        }
    }

    /// <summary>
    /// Gets playlist tracks as child items.
    /// </summary>
    private async Task<IActionResult> GetPlaylistTracks(string playlistId)
    {
        try
        {
            _logger.LogInformation("=== GetPlaylistTracks called === PlaylistId: {PlaylistId}", playlistId);
            
            // Check if this is an external playlist (Deezer/Qobuz) first
            if (PlaylistIdHelper.IsExternalPlaylist(playlistId))
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
                var tracks = await _metadataService.GetPlaylistTracksAsync(provider, externalId);
                return _responseBuilder.CreateItemsResponse(tracks);
            }

            // Check if this is a Spotify playlist (by ID)
            _logger.LogInformation("Spotify Import Enabled: {Enabled}, Configured Playlists: {Count}", 
                _spotifySettings.Enabled, _spotifySettings.Playlists.Count);
            
            if (_spotifySettings.Enabled && _spotifySettings.IsSpotifyPlaylist(playlistId))
            {
                // Get playlist info from Jellyfin to get the name for matching missing tracks
                _logger.LogInformation("Fetching playlist info from Jellyfin for ID: {PlaylistId}", playlistId);
                var (playlistInfo, _) = await _proxyService.GetJsonAsync($"Items/{playlistId}", null, Request.Headers);
                
                if (playlistInfo != null && playlistInfo.RootElement.TryGetProperty("Name", out var nameElement))
                {
                    var playlistName = nameElement.GetString() ?? "";
                    _logger.LogInformation("✓ MATCHED! Intercepting Spotify playlist: {PlaylistName} (ID: {PlaylistId})", 
                        playlistName, playlistId);
                    return await GetSpotifyPlaylistTracksAsync(playlistName, playlistId);
                }
                else
                {
                    _logger.LogWarning("Could not get playlist name from Jellyfin for ID: {PlaylistId}", playlistId);
                }
            }

            // Regular Jellyfin playlist - proxy through
            var endpoint = $"Playlists/{playlistId}/Items";
            if (Request.QueryString.HasValue)
            {
                endpoint = $"{endpoint}{Request.QueryString.Value}";
            }
            
            _logger.LogInformation("Proxying to Jellyfin: {Endpoint}", endpoint);
            var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
            
            return HandleProxyResponse(result, statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting playlist tracks {PlaylistId}", playlistId);
            return _responseBuilder.CreateError(500, "Failed to get playlist tracks");
        }
    }

    /// <summary>
    /// Gets a playlist cover image.
    /// </summary>
    private async Task<IActionResult> GetPlaylistImage(string playlistId)
    {
        try
        {
            // Check cache first (1 hour TTL for playlist images since they can change)
            var cacheKey = $"playlist:image:{playlistId}";
            var cachedImage = await _cache.GetAsync<byte[]>(cacheKey);
            
            if (cachedImage != null)
            {
                _logger.LogDebug("Serving cached playlist image for {PlaylistId}", playlistId);
                return File(cachedImage, "image/jpeg");
            }
            
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(playlistId);
            var playlist = await _metadataService.GetPlaylistAsync(provider, externalId);

            if (playlist == null || string.IsNullOrEmpty(playlist.CoverUrl))
            {
                return NotFound();
            }

            var response = await _proxyService.HttpClient.GetAsync(playlist.CoverUrl);
            if (!response.IsSuccessStatusCode)
            {
                return NotFound();
            }

            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            
            // Cache for 1 hour (playlists can change, so don't cache too long)
            await _cache.SetAsync(cacheKey, imageBytes, TimeSpan.FromHours(1));
            _logger.LogDebug("Cached playlist image for {PlaylistId}", playlistId);
            
            return File(imageBytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get playlist image {PlaylistId}", playlistId);
            return NotFound();
        }
    }

    #endregion

    #region Authentication

    /// <summary>
    /// Authenticates a user by username and password.
    /// This is the primary login endpoint for Jellyfin clients.
    /// </summary>
    [HttpPost("Users/AuthenticateByName")]
    public async Task<IActionResult> AuthenticateByName()
    {
        try
        {
            // Enable buffering to allow multiple reads of the request body
            Request.EnableBuffering();
            
            // Read the request body
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            
            // Reset stream position
            Request.Body.Position = 0;
            
            _logger.LogInformation("Authentication request received");
            // DO NOT log request body or detailed headers - contains password
            
            // Forward to Jellyfin server with client headers - completely transparent proxy
            var (result, statusCode) = await _proxyService.PostJsonAsync("Users/AuthenticateByName", body, Request.Headers);
            
            // Pass through Jellyfin's response exactly as-is (transparent proxy)
            if (result != null)
            {
                var responseJson = result.RootElement.GetRawText();
                
                // On successful auth, extract access token and post session capabilities in background
                if (statusCode == 200)
                {
                    _logger.LogInformation("Authentication successful");
                    
                    // Extract access token from response for session capabilities
                    string? accessToken = null;
                    if (result.RootElement.TryGetProperty("AccessToken", out var tokenEl))
                    {
                        accessToken = tokenEl.GetString();
                    }
                    
                    // Post session capabilities in background if we have a token
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        // Capture token in closure - don't use Request.Headers (will be disposed)
                        var token = accessToken;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                _logger.LogDebug("🔧 Posting session capabilities after authentication");
                                
                                // Build auth header with the new token
                                var authHeaders = new HeaderDictionary
                                {
                                    ["X-Emby-Token"] = token
                                };
                                
                                var capabilities = new
                                {
                                    PlayableMediaTypes = new[] { "Audio" },
                                    SupportedCommands = Array.Empty<string>(),
                                    SupportsMediaControl = false,
                                    SupportsPersistentIdentifier = true,
                                    SupportsSync = false
                                };
                                
                                var capabilitiesJson = JsonSerializer.Serialize(capabilities);
                                var (capResult, capStatus) = await _proxyService.PostJsonAsync("Sessions/Capabilities/Full", capabilitiesJson, authHeaders);
                                
                                if (capStatus == 204 || capStatus == 200)
                                {
                                    _logger.LogDebug("✓ Session capabilities posted after auth ({StatusCode})", capStatus);
                                }
                                else
                                {
                                    _logger.LogDebug("⚠ Session capabilities returned {StatusCode} after auth", capStatus);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to post session capabilities after auth");
                            }
                        });
                    }
                }
                else
                {
                    _logger.LogWarning("Authentication failed - status {StatusCode}", statusCode);
                }
                
                // Return Jellyfin's exact response
                return Content(responseJson, "application/json");
            }
            
            // No response body from Jellyfin - return status code only
            _logger.LogWarning("Authentication request returned {StatusCode} with no response body", statusCode);
            return StatusCode(statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication");
            return StatusCode(500, new { error = $"Authentication error: {ex.Message}" });
        }
    }

    #endregion

    #region Recommendations & Instant Mix

    /// <summary>
    /// Gets similar items for a given item.
    /// For external items, searches for similar content from the provider.
    /// </summary>
    [HttpGet("Items/{itemId}/Similar")]
    [HttpGet("Songs/{itemId}/Similar")]
    [HttpGet("Artists/{itemId}/Similar")]
    public async Task<IActionResult> GetSimilarItems(
        string itemId,
        [FromQuery] int limit = 50,
        [FromQuery] string? fields = null,
        [FromQuery] string? userId = null)
    {
        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);
        
        if (isExternal)
        {
            // Check if this is an artist
            if (itemId.Contains("-artist-", StringComparison.OrdinalIgnoreCase))
            {
                // For external artists, return empty - we don't have similar artist functionality
                _logger.LogDebug("Similar artists not supported for external artist {ItemId}", itemId);
                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = Array.Empty<object>(),
                    TotalRecordCount = 0
                });
            }
            
            try
            {
                // Get the original song to find similar content
                var song = await _metadataService.GetSongAsync(provider!, externalId!);
                if (song == null)
                {
                    return _responseBuilder.CreateJsonResponse(new
                    {
                        Items = Array.Empty<object>(),
                        TotalRecordCount = 0
                    });
                }

                // Search for similar songs using artist and genre
                var searchQuery = $"{song.Artist}";
                var searchResult = await _metadataService.SearchSongsAsync(searchQuery, limit);
                
                // Filter out the original song and convert to Jellyfin format
                var similarSongs = searchResult
                    .Where(s => s.Id != itemId)
                    .Take(limit)
                    .Select(s => _responseBuilder.ConvertSongToJellyfinItem(s))
                    .ToList();

                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = similarSongs,
                    TotalRecordCount = similarSongs.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get similar items for external song {ItemId}", itemId);
                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = Array.Empty<object>(),
                    TotalRecordCount = 0
                });
            }
        }
        
        // For local items, determine the correct endpoint based on the request path
        var endpoint = Request.Path.Value?.Contains("/Artists/", StringComparison.OrdinalIgnoreCase) == true
            ? $"Artists/{itemId}/Similar"
            : $"Items/{itemId}/Similar";
        
        var queryParams = new Dictionary<string, string>
        {
            ["limit"] = limit.ToString()
        };
        
        if (!string.IsNullOrEmpty(fields))
        {
            queryParams["fields"] = fields;
        }
        
        if (!string.IsNullOrEmpty(userId))
        {
            queryParams["userId"] = userId;
        }

        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, queryParams, Request.Headers);
        
        return HandleProxyResponse(result, statusCode, new { Items = Array.Empty<object>(), TotalRecordCount = 0 });
    }

    /// <summary>
    /// Gets an instant mix for a given item.
    /// For external items, creates a mix from the artist's other songs.
    /// </summary>
    [HttpGet("Songs/{itemId}/InstantMix")]
    [HttpGet("Items/{itemId}/InstantMix")]
    public async Task<IActionResult> GetInstantMix(
        string itemId,
        [FromQuery] int limit = 50,
        [FromQuery] string? fields = null,
        [FromQuery] string? userId = null)
    {
        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);
        
        if (isExternal)
        {
            try
            {
                // Get the original song
                var song = await _metadataService.GetSongAsync(provider!, externalId!);
                if (song == null)
                {
                    return _responseBuilder.CreateJsonResponse(new
                    {
                        Items = Array.Empty<object>(),
                        TotalRecordCount = 0
                    });
                }

                // Get artist's albums to build a mix
                var mixSongs = new List<Song>();
                
                // Try to get artist albums
                if (!string.IsNullOrEmpty(song.ExternalProvider) && !string.IsNullOrEmpty(song.ArtistId))
                {
                    var artistExternalId = song.ArtistId.Replace($"ext-{song.ExternalProvider}-artist-", "");
                    var albums = await _metadataService.GetArtistAlbumsAsync(song.ExternalProvider, artistExternalId);
                    
                    // Get songs from a few albums
                    foreach (var album in albums.Take(3))
                    {
                        var fullAlbum = await _metadataService.GetAlbumAsync(song.ExternalProvider, album.ExternalId!);
                        if (fullAlbum != null)
                        {
                            mixSongs.AddRange(fullAlbum.Songs);
                        }
                        
                        if (mixSongs.Count >= limit) break;
                    }
                }
                
                // If we don't have enough songs, search for more by the artist
                if (mixSongs.Count < limit)
                {
                    var searchResult = await _metadataService.SearchSongsAsync(song.Artist, limit);
                    mixSongs.AddRange(searchResult.Where(s => !mixSongs.Any(m => m.Id == s.Id)));
                }

                // Shuffle and limit
                var random = new Random();
                var shuffledMix = mixSongs
                    .Where(s => s.Id != itemId) // Exclude the seed song
                    .OrderBy(_ => random.Next())
                    .Take(limit)
                    .Select(s => _responseBuilder.ConvertSongToJellyfinItem(s))
                    .ToList();

                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = shuffledMix,
                    TotalRecordCount = shuffledMix.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create instant mix for external song {ItemId}", itemId);
                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = Array.Empty<object>(),
                    TotalRecordCount = 0
                });
            }
        }
        
        // For local items, proxy to Jellyfin
        var queryParams = new Dictionary<string, string>
        {
            ["limit"] = limit.ToString()
        };
        
        if (!string.IsNullOrEmpty(fields))
        {
            queryParams["fields"] = fields;
        }
        
        if (!string.IsNullOrEmpty(userId))
        {
            queryParams["userId"] = userId;
        }

        var (result, statusCode) = await _proxyService.GetJsonAsync($"Songs/{itemId}/InstantMix", queryParams, Request.Headers);
        
        return HandleProxyResponse(result, statusCode, new { Items = Array.Empty<object>(), TotalRecordCount = 0 });
    }

    #endregion

    #region Playback Session Reporting

    #region Session Management

    /// <summary>
    /// Reports session capabilities. Required for Jellyfin to track active sessions.
    /// Handles both POST (with body) and GET (query params only) methods.
    /// </summary>
    [HttpPost("Sessions/Capabilities")]
    [HttpPost("Sessions/Capabilities/Full")]
    [HttpGet("Sessions/Capabilities")]
    [HttpGet("Sessions/Capabilities/Full")]
    public async Task<IActionResult> ReportCapabilities()
    {
        try
        {
            var method = Request.Method;
            var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";
            
            _logger.LogDebug("📡 Session capabilities reported - Method: {Method}, Query: {Query}", method, queryString);
            _logger.LogInformation("Headers: {Headers}", 
                string.Join(", ", Request.Headers.Where(h => h.Key.Contains("Auth", StringComparison.OrdinalIgnoreCase) || h.Key.Contains("Device", StringComparison.OrdinalIgnoreCase) || h.Key.Contains("Client", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}={h.Value}")));
            
            // Forward to Jellyfin with query string and headers
            var endpoint = $"Sessions/Capabilities{queryString}";
            
            // Read body if present (POST requests)
            string body = "{}";
            if (method == "POST" && Request.ContentLength > 0)
            {
                Request.EnableBuffering();
                using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync();
                }
                Request.Body.Position = 0;
                _logger.LogInformation("Capabilities body: {Body}", body);
            }
            
            var (result, statusCode) = await _proxyService.PostJsonAsync(endpoint, body, Request.Headers);
            
            if (statusCode == 204 || statusCode == 200)
            {
                _logger.LogDebug("✓ Session capabilities forwarded to Jellyfin ({StatusCode})", statusCode);
            }
            else if (statusCode == 401)
            {
                _logger.LogDebug("⚠ Jellyfin returned 401 for capabilities (token expired)");
            }
            else
            {
                _logger.LogWarning("⚠ Jellyfin returned {StatusCode} for capabilities", statusCode);
            }
            
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report session capabilities");
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Reports playback start. Handles both local and external tracks.
    /// For local tracks, forwards to Jellyfin. For external tracks, logs locally.
    /// Also ensures session is initialized if this is the first report from a device.
    /// </summary>
    [HttpPost("Sessions/Playing")]
    public async Task<IActionResult> ReportPlaybackStart()
    {
        try
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }
            Request.Body.Position = 0;

            _logger.LogDebug("📻 Playback START reported");

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            string? itemName = null;
            long? positionTicks = null;
            
            if (doc.RootElement.TryGetProperty("ItemId", out var itemIdProp))
            {
                itemId = itemIdProp.GetString();
            }
            
            if (doc.RootElement.TryGetProperty("ItemName", out var itemNameProp))
            {
                itemName = itemNameProp.GetString();
            }
            
            if (doc.RootElement.TryGetProperty("PositionTicks", out var posProp))
            {
                positionTicks = posProp.GetInt64();
            }
            
            // Track the playing item for scrobbling on session cleanup
            var (deviceId, client, device, version) = ExtractDeviceInfo(Request.Headers);
            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(itemId))
            {
                _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                if (isExternal)
                {
                    _logger.LogInformation("🎵 External track playback started: {Name} ({Provider}/{ExternalId})", 
                        itemName ?? "Unknown", provider, externalId);
                    
                    // Proactively fetch lyrics in background for external tracks
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await PrefetchLyricsForTrackAsync(itemId, isExternal: true, provider, externalId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to prefetch lyrics for external track {ItemId}", itemId);
                        }
                    });
                    
                    // Create a ghost/fake item to report to Jellyfin so "Now Playing" shows up
                    // Generate a deterministic UUID from the external ID
                    var ghostUuid = GenerateUuidFromString(itemId);
                    
                    // Build minimal playback start with just the ghost UUID
                    // Don't include the Item object - Jellyfin will just track the session without item details
                    var playbackStart = new
                    {
                        ItemId = ghostUuid,
                        PositionTicks = positionTicks ?? 0,
                        CanSeek = true,
                        IsPaused = false,
                        IsMuted = false,
                        PlayMethod = "DirectPlay"
                    };
                    
                    var playbackJson = JsonSerializer.Serialize(playbackStart);
                    _logger.LogDebug("📤 Sending ghost playback start for external track: {Json}", playbackJson);
                    
                    // Forward to Jellyfin with ghost UUID
                    var (ghostResult, ghostStatusCode) = await _proxyService.PostJsonAsync("Sessions/Playing", playbackJson, Request.Headers);
                    
                    if (ghostStatusCode == 204 || ghostStatusCode == 200)
                    {
                        _logger.LogDebug("✓ Ghost playback start forwarded to Jellyfin for external track ({StatusCode})", ghostStatusCode);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Ghost playback start returned status {StatusCode} for external track", ghostStatusCode);
                    }
                    
                    return NoContent();
                }
                
                _logger.LogInformation("🎵 Local track playback started: {Name} (ID: {ItemId})", 
                    itemName ?? "Unknown", itemId);
                
                // Proactively fetch lyrics in background for local tracks
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await PrefetchLyricsForTrackAsync(itemId, isExternal: false, null, null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to prefetch lyrics for local track {ItemId}", itemId);
                    }
                });
            }

            // For local tracks, forward playback start to Jellyfin FIRST
            _logger.LogDebug("Forwarding playback start to Jellyfin...");
            
            // Fetch full item details to include in playback report
            try
            {
                var (itemResult, itemStatus) = await _proxyService.GetJsonAsync($"Items/{itemId}", null, Request.Headers);
                if (itemResult != null && itemStatus == 200)
                {
                    var item = itemResult.RootElement;
                    _logger.LogInformation("📦 Fetched item details for playback report");
                    
                    // Build playback start info - Jellyfin will fetch item details itself
                    var playbackStart = new
                    {
                        ItemId = itemId,
                        PositionTicks = positionTicks ?? 0,
                        // Let Jellyfin fetch the item details - don't include NowPlayingItem
                    };
                    
                    var playbackJson = JsonSerializer.Serialize(playbackStart);
                    _logger.LogInformation("📤 Sending playback start: {Json}", playbackJson);
                    
                    var (result, statusCode) = await _proxyService.PostJsonAsync("Sessions/Playing", playbackJson, Request.Headers);
                    
                    if (statusCode == 204 || statusCode == 200)
                    {
                        _logger.LogDebug("✓ Playback start forwarded to Jellyfin ({StatusCode})", statusCode);
                        
                        // NOW ensure session exists with capabilities (after playback is reported)
                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            var sessionCreated = await _sessionManager.EnsureSessionAsync(deviceId, client ?? "Unknown", device ?? "Unknown", version ?? "1.0", Request.Headers);
                            if (sessionCreated)
                            {
                                _logger.LogDebug("✓ SESSION: Session ensured for device {DeviceId} after playback start", deviceId);
                            }
                            else
                            {
                                _logger.LogWarning("⚠️ SESSION: Failed to ensure session for device {DeviceId}", deviceId);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ SESSION: No device ID found in headers for playback start");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("⚠️  Playback start returned status {StatusCode}", statusCode);
                    }
                }
                else
                {
                    _logger.LogWarning("⚠️  Could not fetch item details ({StatusCode}), sending basic playback start", itemStatus);
                    // Fall back to basic playback start
                    var (result, statusCode) = await _proxyService.PostJsonAsync("Sessions/Playing", body, Request.Headers);
                    if (statusCode == 204 || statusCode == 200)
                    {
                        _logger.LogDebug("✓ Basic playback start forwarded to Jellyfin ({StatusCode})", statusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send playback start, trying basic");
                // Fall back to basic playback start
                var (result, statusCode) = await _proxyService.PostJsonAsync("Sessions/Playing", body, Request.Headers);
                if (statusCode == 204 || statusCode == 200)
                {
                    _logger.LogInformation("✓ Basic playback start forwarded to Jellyfin ({StatusCode})", statusCode);
                }
            }
            
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report playback start");
            return NoContent(); // Return success anyway to not break playback
        }
    }

    /// <summary>
    /// Reports playback progress. Handles both local and external tracks.
    /// </summary>
    [HttpPost("Sessions/Playing/Progress")]
    public async Task<IActionResult> ReportPlaybackProgress()
    {
        try
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }
            Request.Body.Position = 0;

            // Update session activity
            var (deviceId, _, _, _) = ExtractDeviceInfo(Request.Headers);
            if (!string.IsNullOrEmpty(deviceId))
            {
                _sessionManager.UpdateActivity(deviceId);
            }

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            long? positionTicks = null;
            
            if (doc.RootElement.TryGetProperty("ItemId", out var itemIdProp))
            {
                itemId = itemIdProp.GetString();
            }
            if (doc.RootElement.TryGetProperty("PositionTicks", out var posProp))
            {
                positionTicks = posProp.GetInt64();
            }
            
            // Track the playing item for scrobbling on session cleanup
            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(itemId))
            {
                _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
            }
            
            if (!string.IsNullOrEmpty(itemId))
            {
                var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                if (isExternal)
                {
                    // For external tracks, report progress with ghost UUID to Jellyfin
                    var ghostUuid = GenerateUuidFromString(itemId);
                    
                    // Build progress report with ghost UUID
                    var progressReport = new
                    {
                        ItemId = ghostUuid,
                        PositionTicks = positionTicks ?? 0,
                        IsPaused = false,
                        IsMuted = false,
                        CanSeek = true,
                        PlayMethod = "DirectPlay"
                    };
                    
                    var progressJson = JsonSerializer.Serialize(progressReport);
                    
                    // Forward to Jellyfin with ghost UUID
                    var (progressResult, progressStatusCode) = await _proxyService.PostJsonAsync("Sessions/Playing/Progress", progressJson, Request.Headers);
                    
                    // Log progress occasionally for debugging (every ~30 seconds)
                    if (positionTicks.HasValue)
                    {
                        var position = TimeSpan.FromTicks(positionTicks.Value);
                        if (position.Seconds % 30 == 0 && position.Milliseconds < 500)
                        {
                            _logger.LogDebug("▶️ External track progress: {Position:mm\\:ss} ({Provider}/{ExternalId}) - Status: {StatusCode}", 
                                position, provider, externalId, progressStatusCode);
                        }
                    }
                    
                    return NoContent();
                }
                
                // Log progress for local tracks (only every ~10 seconds to avoid spam)
                if (positionTicks.HasValue)
                {
                    var position = TimeSpan.FromTicks(positionTicks.Value);
                    // Only log at 10-second intervals
                    if (position.Seconds % 10 == 0 && position.Milliseconds < 500)
                    {
                        _logger.LogDebug("▶️ Progress: {Position:mm\\:ss} for item {ItemId}", position, itemId);
                    }
                }
            }

            // For local tracks, forward to Jellyfin
            _logger.LogDebug("📤 Sending playback progress body: {Body}", body);
            
            var (result, statusCode) = await _proxyService.PostJsonAsync("Sessions/Playing/Progress", body, Request.Headers);
            
            if (statusCode != 204 && statusCode != 200)
            {
                _logger.LogWarning("⚠️ Progress report returned {StatusCode} for item {ItemId}", statusCode, itemId);
            }
            
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to report playback progress");
            return NoContent();
        }
    }

    /// <summary>
    /// Reports playback stopped. Handles both local and external tracks.
    /// </summary>
    [HttpPost("Sessions/Playing/Stopped")]
    public async Task<IActionResult> ReportPlaybackStopped()
    {
        try
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }
            Request.Body.Position = 0;

            _logger.LogDebug("⏹️  Playback STOPPED reported");

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            string? itemName = null;
            long? positionTicks = null;
            string? deviceId = null;
            
            if (doc.RootElement.TryGetProperty("ItemId", out var itemIdProp))
            {
                itemId = itemIdProp.GetString();
            }
            
            if (doc.RootElement.TryGetProperty("ItemName", out var itemNameProp))
            {
                itemName = itemNameProp.GetString();
            }
            
            if (doc.RootElement.TryGetProperty("PositionTicks", out var posProp))
            {
                positionTicks = posProp.GetInt64();
            }

            // Try to get device ID from headers for session management
            if (Request.Headers.TryGetValue("X-Emby-Device-Id", out var deviceIdHeader))
            {
                deviceId = deviceIdHeader.FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                if (isExternal)
                {
                    var position = positionTicks.HasValue 
                        ? TimeSpan.FromTicks(positionTicks.Value).ToString(@"mm\:ss")
                        : "unknown";
                    _logger.LogInformation("🎵 External track playback stopped: {Name} at {Position} ({Provider}/{ExternalId})", 
                        itemName ?? "Unknown", position, provider, externalId);
                    
                    // Report stop to Jellyfin with ghost UUID
                    var ghostUuid = GenerateUuidFromString(itemId);
                    
                    var stopInfo = new
                    {
                        ItemId = ghostUuid,
                        PositionTicks = positionTicks ?? 0
                    };
                    
                    var stopJson = JsonSerializer.Serialize(stopInfo);
                    _logger.LogDebug("📤 Sending ghost playback stop for external track: {Json}", stopJson);
                    
                    var (stopResult, stopStatusCode) = await _proxyService.PostJsonAsync("Sessions/Playing/Stopped", stopJson, Request.Headers);
                    
                    if (stopStatusCode == 204 || stopStatusCode == 200)
                    {
                        _logger.LogDebug("✓ Ghost playback stop forwarded to Jellyfin ({StatusCode})", stopStatusCode);
                    }
                    
                    return NoContent();
                }
                
                _logger.LogInformation("🎵 Local track playback stopped: {Name} (ID: {ItemId})", 
                    itemName ?? "Unknown", itemId);
            }

            // For local tracks, forward to Jellyfin
            _logger.LogDebug("Forwarding playback stop to Jellyfin...");
            
            // Log the body being sent for debugging
            _logger.LogInformation("📤 Sending playback stop body: {Body}", body);
            
            // Validate that body is not empty
            if (string.IsNullOrWhiteSpace(body) || body == "{}")
            {
                _logger.LogWarning("⚠️  Playback stop body is empty, building minimal valid payload");
                // Build a minimal valid PlaybackStopInfo
                var stopInfo = new
                {
                    ItemId = itemId,
                    PositionTicks = positionTicks ?? 0
                };
                body = JsonSerializer.Serialize(stopInfo);
                _logger.LogInformation("📤 Built playback stop body: {Body}", body);
            }
            
            var (result, statusCode) = await _proxyService.PostJsonAsync("Sessions/Playing/Stopped", body, Request.Headers);
            
            if (statusCode == 204 || statusCode == 200)
            {
                _logger.LogDebug("✓ Playback stop forwarded to Jellyfin ({StatusCode})", statusCode);
            }
            else if (statusCode == 401)
            {
                _logger.LogDebug("Playback stop returned 401 (token expired)");
            }
            else
            {
                _logger.LogWarning("Playback stop forward failed with status {StatusCode}", statusCode);
            }
            
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report playback stopped");
            return NoContent();
        }
    }

    /// <summary>
    /// Pings a playback session to keep it alive.
    /// </summary>
    [HttpPost("Sessions/Playing/Ping")]
    public async Task<IActionResult> PingPlaybackSession([FromQuery] string playSessionId)
    {
        try
        {
            _logger.LogDebug("Playback session ping: {SessionId}", playSessionId);
            
            // Forward to Jellyfin
            var endpoint = $"Sessions/Playing/Ping?playSessionId={Uri.EscapeDataString(playSessionId)}";
            var (result, statusCode) = await _proxyService.PostJsonAsync(endpoint, "{}", Request.Headers);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to ping playback session");
            return NoContent();
        }
    }

    /// <summary>
    /// Catch-all for any other session-related requests.
    /// <summary>
    /// Catch-all proxy for any other session-related endpoints we haven't explicitly implemented.
    /// This ensures all session management calls get proxied to Jellyfin.
    /// Examples: GET /Sessions, POST /Sessions/Logout, etc.
    /// </summary>
    [HttpGet("Sessions")]
    [HttpPost("Sessions")]
    [HttpGet("Sessions/{**path}")]
    [HttpPost("Sessions/{**path}")]
    [HttpPut("Sessions/{**path}")]
    [HttpDelete("Sessions/{**path}")]
    public async Task<IActionResult> ProxySessionRequest(string? path = null)
    {
        try
        {
            var method = Request.Method;
            var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";
            var endpoint = string.IsNullOrEmpty(path) ? $"Sessions{queryString}" : $"Sessions/{path}{queryString}";
            
            _logger.LogDebug("🔄 Proxying session request: {Method} {Endpoint}", method, endpoint);
            _logger.LogDebug("Session proxy headers: {Headers}", 
                string.Join(", ", Request.Headers.Where(h => h.Key.Contains("Auth", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}={h.Value}")));
            
            // Read body if present
            string body = "{}";
            if ((method == "POST" || method == "PUT") && Request.ContentLength > 0)
            {
                Request.EnableBuffering();
                using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync();
                }
                Request.Body.Position = 0;
                _logger.LogDebug("Session proxy body: {Body}", body);
            }
            
            // Forward to Jellyfin
            var (result, statusCode) = method switch
            {
                "GET" => await _proxyService.GetJsonAsync(endpoint, null, Request.Headers),
                "POST" => await _proxyService.PostJsonAsync(endpoint, body, Request.Headers),
                "PUT" => await _proxyService.PostJsonAsync(endpoint, body, Request.Headers), // Use POST for PUT
                "DELETE" => await _proxyService.PostJsonAsync(endpoint, body, Request.Headers), // Use POST for DELETE
                _ => (null, 405)
            };
            
            if (result != null)
            {
                _logger.LogDebug("✓ Session request proxied successfully ({StatusCode})", statusCode);
                return new JsonResult(result.RootElement.Clone());
            }
            
            _logger.LogDebug("✓ Session request proxied ({StatusCode}, no body)", statusCode);
            return StatusCode(statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy session request: {Path}", path);
            return StatusCode(500);
        }
    }

    #endregion // Session Management

    #endregion // Playback Session Reporting

    #region System & Proxy

    /// <summary>
    /// Returns public server info.
    /// </summary>
    [HttpGet("System/Info/Public")]
    public async Task<IActionResult> GetPublicSystemInfo()
    {
        var (success, serverName, version) = await _proxyService.TestConnectionAsync();

        return _responseBuilder.CreateJsonResponse(new
        {
            LocalAddress = Request.Host.ToString(),
            ServerName = serverName ?? "Allstarr",
            Version = version ?? "1.0.0",
            ProductName = "Allstarr (Jellyfin Proxy)",
            OperatingSystem = Environment.OSVersion.Platform.ToString(),
            Id = _settings.DeviceId,
            StartupWizardCompleted = true
        });
    }

    /// <summary>
    /// Root path handler - redirects to Jellyfin web UI.
    /// </summary>
    [HttpGet("", Order = 99)]
    public async Task<IActionResult> ProxyRootRequest()
    {
        return await ProxyRequest("web/index.html");
    }

    /// <summary>
    /// Catch-all endpoint that proxies unhandled requests to Jellyfin transparently.
    /// This route has the lowest priority and should only match requests that don't have SearchTerm.
    /// Blocks dangerous admin endpoints for security.
    /// </summary>
    [HttpGet("{**path}", Order = 100)]
    [HttpPost("{**path}", Order = 100)]
    public async Task<IActionResult> ProxyRequest(string path)
    {
        // Log session-related requests prominently to debug missing capabilities call
        if (path.Contains("session", StringComparison.OrdinalIgnoreCase) || 
            path.Contains("capabilit", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("🔍 SESSION/CAPABILITY REQUEST: {Method} /{Path}{Query}", Request.Method, path, Request.QueryString);
        }
        else
        {
            _logger.LogDebug("ProxyRequest: {Method} /{Path}", Request.Method, path);
        }
        
        // Log endpoint usage to file for analysis
        await LogEndpointUsageAsync(path, Request.Method);
        
        // Block dangerous admin endpoints
        var blockedPrefixes = new[]
        {
            "system/restart",           // Server restart
            "system/shutdown",          // Server shutdown
            "system/configuration",     // System configuration changes
            "system/logs",              // Server logs access
            "system/activitylog",       // Activity log access
            "plugins/",                 // Plugin management (install/uninstall/configure)
            "scheduledtasks/",          // Scheduled task management
            "startup/",                 // Initial server setup
            "users/new",                // User creation
            "library/refresh",          // Library scan (expensive operation)
            "library/virtualfolders",   // Library folder management
            "branding/",                // Branding configuration
            "displaypreferences/",      // Display preferences (if not user-specific)
            "notifications/admin"       // Admin notifications
        };
        
        // Check if path matches any blocked prefix
        if (blockedPrefixes.Any(prefix => 
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("BLOCKED: Access denied to admin endpoint: {Path} from {IP}", 
                path, 
                HttpContext.Connection.RemoteIpAddress);
            return StatusCode(403, new 
            { 
                error = "Access to administrative endpoints is not allowed through this proxy",
                path = path
            });
        }
        
        // Intercept Spotify playlist requests by ID
        if (_spotifySettings.Enabled && 
            path.StartsWith("playlists/", StringComparison.OrdinalIgnoreCase) && 
            path.Contains("/items", StringComparison.OrdinalIgnoreCase))
        {
            // Extract playlist ID from path: playlists/{id}/items
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("playlists", StringComparison.OrdinalIgnoreCase))
            {
                var playlistId = parts[1];
                
                _logger.LogInformation("=== PLAYLIST REQUEST ===");
                _logger.LogInformation("Playlist ID: {PlaylistId}", playlistId);
                _logger.LogInformation("Spotify Enabled: {Enabled}", _spotifySettings.Enabled);
                _logger.LogInformation("Configured Playlists: {Playlists}", string.Join(", ", _spotifySettings.Playlists.Select(p => $"{p.Name}:{p.Id}")));
                _logger.LogInformation("Is configured: {IsConfigured}", _spotifySettings.IsSpotifyPlaylist(playlistId));
                
                // Check if this playlist ID is configured for Spotify injection
                if (_spotifySettings.IsSpotifyPlaylist(playlistId))
                {
                    _logger.LogInformation("========================================");
                    _logger.LogInformation("=== INTERCEPTING SPOTIFY PLAYLIST ===");
                    _logger.LogInformation("Playlist ID: {PlaylistId}", playlistId);
                    _logger.LogInformation("========================================");
                    return await GetPlaylistTracks(playlistId);
                }
            }
        }
        
        // Handle non-JSON responses (images, robots.txt, etc.)
        if (path.Contains("/Images/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || 
            path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            var fullPath = path;
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{path}{Request.QueryString.Value}";
            }
            
            var url = $"{_settings.Url?.TrimEnd('/')}/{fullPath}";
            
            try
            {
                // Forward authentication headers for image requests
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                
                // Forward auth headers from client
                if (Request.Headers.TryGetValue("X-Emby-Authorization", out var embyAuth))
                {
                    request.Headers.TryAddWithoutValidation("X-Emby-Authorization", embyAuth.ToString());
                }
                else if (Request.Headers.TryGetValue("Authorization", out var auth))
                {
                    var authValue = auth.ToString();
                    if (authValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase) || 
                        authValue.Contains("Token=", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", authValue);
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", authValue);
                    }
                }
                
                var response = await _proxyService.HttpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode);
                }
                
                var contentBytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                return File(contentBytes, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to proxy binary request for {Path}", path);
                return NotFound();
            }
        }

        // Check if this is a search request that should be handled by specific endpoints
        var searchTerm = Request.Query["SearchTerm"].ToString();
        
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            _logger.LogInformation("ProxyRequest intercepting search request: Path={Path}, SearchTerm={SearchTerm}", path, searchTerm);
            
            // Item search: /users/{userId}/items or /items
            if (path.EndsWith("/items", StringComparison.OrdinalIgnoreCase) || path.Equals("items", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Redirecting to SearchItems");
                return await SearchItems(
                    searchTerm: searchTerm,
                    includeItemTypes: Request.Query["IncludeItemTypes"],
                    limit: int.TryParse(Request.Query["Limit"], out var limit) ? limit : 100,
                    startIndex: int.TryParse(Request.Query["StartIndex"], out var start) ? start : 0,
                    parentId: Request.Query["ParentId"],
                    sortBy: Request.Query["SortBy"],
                    recursive: Request.Query["Recursive"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase),
                    userId: path.Contains("/users/", StringComparison.OrdinalIgnoreCase) && path.Split('/').Length > 2 ? path.Split('/')[2] : null);
            }
            
            // Artist search: /artists/albumartists or /artists
            if (path.Contains("/artists", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Redirecting to GetArtists");
                return await GetArtists(
                    searchTerm: searchTerm,
                    limit: int.TryParse(Request.Query["Limit"], out var limit) ? limit : 50,
                    startIndex: int.TryParse(Request.Query["StartIndex"], out var start) ? start : 0);
            }
        }
        
        try
        {
            // Include query string in the path
            var fullPath = path;
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{path}{Request.QueryString.Value}";
            }
            
            JsonDocument? result;
            int statusCode;
            
            if (HttpContext.Request.Method == HttpMethod.Post.Method)
            {
                // Enable buffering BEFORE any reads
                Request.EnableBuffering();
                
                // Log request details for debugging
                _logger.LogInformation("POST request to {Path}: Method={Method}, ContentType={ContentType}, ContentLength={ContentLength}", 
                    fullPath, Request.Method, Request.ContentType, Request.ContentLength);
                
                // Read body using StreamReader with proper encoding
                string body;
                using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync();
                }
                
                // Reset stream position after reading so it can be read again if needed
                Request.Body.Position = 0;
                
                if (string.IsNullOrWhiteSpace(body))
                {
                    _logger.LogWarning("Empty POST body received from client for {Path}, ContentLength={ContentLength}, ContentType={ContentType}", 
                        fullPath, Request.ContentLength, Request.ContentType);
                    
                    // Log all headers to debug
                    _logger.LogWarning("Request headers: {Headers}", 
                        string.Join(", ", Request.Headers.Select(h => $"{h.Key}={h.Value}")));
                }
                else
                {
                    _logger.LogInformation("POST body received from client for {Path}: {BodyLength} bytes, ContentType={ContentType}", 
                        fullPath, body.Length, Request.ContentType);
                    
                    // Always log body content for playback endpoints to debug the issue
                    if (fullPath.Contains("Playing", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("POST body content from client: {Body}", body);
                    }
                }
                
                (result, statusCode) = await _proxyService.PostJsonAsync(fullPath, body, Request.Headers);
            }
            else
            {
                // Forward GET requests transparently with authentication headers and query string
                (result, statusCode) = await _proxyService.GetJsonAsync(fullPath, null, Request.Headers);
            }
            
            // Handle different status codes
            if (result == null)
            {
                // No body - return the status code from Jellyfin
                if (statusCode == 204)
                {
                    return NoContent();
                }
                else if (statusCode == 401)
                {
                    return Unauthorized();
                }
                else if (statusCode == 403)
                {
                    return Forbid();
                }
                else if (statusCode == 404)
                {
                    return NotFound();
                }
                else if (statusCode >= 400 && statusCode < 500)
                {
                    return StatusCode(statusCode);
                }
                else if (statusCode >= 500)
                {
                    return StatusCode(statusCode);
                }
                
                // Default to 204 for 2xx responses with no body
                return NoContent();
            }

            // Modify response if it contains Spotify playlists to update ChildCount
            // Only check for Items if the response is an object (not a string or array)
            if (_spotifySettings.Enabled && 
                result.RootElement.ValueKind == JsonValueKind.Object && 
                result.RootElement.TryGetProperty("Items", out var items))
            {
                _logger.LogInformation("Response has Items property, checking for Spotify playlists to update counts");
                result = await UpdateSpotifyPlaylistCounts(result);
            }

            // Return the raw JSON element directly to avoid deserialization issues with simple types
            return new JsonResult(result.RootElement.Clone());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxy request failed for {Path}", path);
            return _responseBuilder.CreateError(502, $"Proxy error: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Helper to handle proxy responses with proper status code handling.
    /// </summary>
    private IActionResult HandleProxyResponse(JsonDocument? result, int statusCode, object? fallbackValue = null)
    {
        if (result != null)
        {
            return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
        }
        
        // Handle error status codes
        if (statusCode == 401)
        {
            return Unauthorized();
        }
        else if (statusCode == 403)
        {
            return Forbid();
        }
        else if (statusCode == 404)
        {
            return NotFound();
        }
        else if (statusCode >= 400)
        {
            return StatusCode(statusCode);
        }
        
        // Success with no body - return fallback or empty
        if (fallbackValue != null)
        {
            return new JsonResult(fallbackValue);
        }
        
        return NoContent();
    }

    /// <summary>
    /// Updates ChildCount for Spotify playlists in the response to show total tracks (local + matched).
    /// </summary>
    private async Task<JsonDocument> UpdateSpotifyPlaylistCounts(JsonDocument response)
    {
        try
        {
            if (!response.RootElement.TryGetProperty("Items", out var items))
            {
                return response;
            }

            var itemsArray = items.EnumerateArray().ToList();
            var modified = false;
            var updatedItems = new List<Dictionary<string, object>>();

            _logger.LogInformation("Checking {Count} items for Spotify playlists", itemsArray.Count);

            foreach (var item in itemsArray)
            {
                var itemDict = JsonSerializer.Deserialize<Dictionary<string, object>>(item.GetRawText());
                if (itemDict == null)
                {
                    continue;
                }

                // Check if this is a Spotify playlist
                if (item.TryGetProperty("Id", out var idProp))
                {
                    var playlistId = idProp.GetString();
                    _logger.LogDebug("Checking item with ID: {Id}", playlistId);
                    
                    if (!string.IsNullOrEmpty(playlistId) && _spotifySettings.IsSpotifyPlaylist(playlistId))
                    {
                        _logger.LogInformation("Found Spotify playlist: {Id}", playlistId);
                        
                        // This is a Spotify playlist - get the actual track count
                        var playlistConfig = _spotifySettings.GetPlaylistByJellyfinId(playlistId);
                        
                        if (playlistConfig != null)
                        {
                            _logger.LogInformation("Found playlist config for Jellyfin ID {JellyfinId}: {Name} (Spotify ID: {SpotifyId})", 
                                playlistId, playlistConfig.Name, playlistConfig.Id);
                            var playlistName = playlistConfig.Name;
                            
                            // Get matched external tracks (tracks that were successfully downloaded/matched)
                            var matchedTracksKey = $"spotify:matched:ordered:{playlistName}";
                            var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);
                            
                            _logger.LogDebug("Cache lookup for {Key}: {Count} matched tracks", 
                                matchedTracksKey, matchedTracks?.Count ?? 0);
                            
                            // Fallback to legacy cache format
                            if (matchedTracks == null || matchedTracks.Count == 0)
                            {
                                var legacyKey = $"spotify:matched:{playlistName}";
                                var legacySongs = await _cache.GetAsync<List<Song>>(legacyKey);
                                if (legacySongs != null && legacySongs.Count > 0)
                                {
                                    matchedTracks = legacySongs.Select((s, i) => new MatchedTrack
                                    {
                                        Position = i,
                                        MatchedSong = s
                                    }).ToList();
                                    _logger.LogDebug("Loaded {Count} tracks from legacy cache", matchedTracks.Count);
                                }
                            }
                            
                            // Try loading from file cache if Redis is empty
                            if (matchedTracks == null || matchedTracks.Count == 0)
                            {
                                var fileItems = await LoadPlaylistItemsFromFile(playlistName);
                                if (fileItems != null && fileItems.Count > 0)
                                {
                                    _logger.LogInformation("💿 Loaded {Count} playlist items from file cache for count update", fileItems.Count);
                                    // Use file cache count directly
                                    itemDict["ChildCount"] = fileItems.Count;
                                    modified = true;
                                }
                            }
                            
                            // Only fetch from Jellyfin if we didn't get count from file cache
                            if (!itemDict.ContainsKey("ChildCount") || 
                                (itemDict["ChildCount"] is JsonElement childCountElement && childCountElement.GetInt32() == 0) ||
                                (itemDict["ChildCount"] is int childCountInt && childCountInt == 0))
                            {
                                // Get local tracks count from Jellyfin
                                var localTracksCount = 0;
                                try
                                {
                                    // Include UserId parameter to avoid 401 Unauthorized
                                    var userId = _settings.UserId;
                                    var playlistItemsUrl = $"Playlists/{playlistId}/Items";
                                    var queryParams = new Dictionary<string, string>();
                                    if (!string.IsNullOrEmpty(userId))
                                    {
                                        queryParams["UserId"] = userId;
                                    }
                                    
                                    var (localTracksResponse, _) = await _proxyService.GetJsonAsyncInternal(
                                        playlistItemsUrl, 
                                        queryParams);
                                    
                                    if (localTracksResponse != null && 
                                        localTracksResponse.RootElement.TryGetProperty("Items", out var localItems))
                                    {
                                        localTracksCount = localItems.GetArrayLength();
                                        _logger.LogInformation("Found {Count} total items in Jellyfin playlist {Name}", 
                                            localTracksCount, playlistName);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to get local tracks count for {Name}", playlistName);
                                }
                                
                                // Count external matched tracks (not local)
                                var externalMatchedCount = 0;
                                if (matchedTracks != null)
                                {
                                    externalMatchedCount = matchedTracks.Count(t => t.MatchedSong != null && !t.MatchedSong.IsLocal);
                                }
                                
                                // Total available tracks = local tracks in Jellyfin + external matched tracks
                                // This represents what users will actually hear when playing the playlist
                                var totalAvailableCount = localTracksCount + externalMatchedCount;
                                
                                if (totalAvailableCount > 0)
                                {
                                    // Update ChildCount to show actual available tracks
                                    itemDict["ChildCount"] = totalAvailableCount;
                                    modified = true;
                                    _logger.LogInformation("✓ Updated ChildCount for Spotify playlist {Name} to {Total} ({Local} local + {External} external)", 
                                        playlistName, totalAvailableCount, localTracksCount, externalMatchedCount);
                                }
                                else
                                {
                                    _logger.LogWarning("No tracks found for {Name} ({Local} local + {External} external = {Total} total)", 
                                        playlistName, localTracksCount, externalMatchedCount, totalAvailableCount);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("No playlist config found for Jellyfin ID {JellyfinId} - skipping count update", playlistId);
                        }
                    }
                }

                updatedItems.Add(itemDict);
            }

            if (!modified)
            {
                _logger.LogInformation("No Spotify playlists found to update");
                return response;
            }

            _logger.LogInformation("Modified {Count} Spotify playlists, rebuilding response", 
                updatedItems.Count(i => i.ContainsKey("ChildCount")));

            // Rebuild the response with updated items
            var responseDict = JsonSerializer.Deserialize<Dictionary<string, object>>(response.RootElement.GetRawText());
            if (responseDict != null)
            {
                responseDict["Items"] = updatedItems;
                var updatedJson = JsonSerializer.Serialize(responseDict);
                return JsonDocument.Parse(updatedJson);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Spotify playlist counts");
            return response;
        }
    }

    /// <summary>
    /// Logs endpoint usage to a file for analysis.
    /// Creates a CSV file with timestamp, method, path, and query string.
    /// </summary>
    private async Task LogEndpointUsageAsync(string path, string method)
    {
        try
        {
            var logDir = "/app/cache/endpoint-usage";
            Directory.CreateDirectory(logDir);
            
            var logFile = Path.Combine(logDir, "endpoints.csv");
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";
            
            // Sanitize path and query for CSV (remove commas, quotes, newlines)
            var sanitizedPath = path.Replace(",", ";").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
            var sanitizedQuery = queryString.Replace(",", ";").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
            
            var logLine = $"{timestamp},{method},{sanitizedPath},{sanitizedQuery}\n";
            
            // Append to file (thread-safe)
            await System.IO.File.AppendAllTextAsync(logFile, logLine);
        }
        catch (Exception ex)
        {
            // Don't let logging failures break the request
            _logger.LogDebug(ex, "Failed to log endpoint usage");
        }
    }

    private static string[]? ParseItemTypes(string? includeItemTypes)
    {
        if (string.IsNullOrWhiteSpace(includeItemTypes))
        {
            return null;
        }

        return includeItemTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            _ => "audio/mpeg"
        };
    }

    /// <summary>
    /// Scores search results based on fuzzy matching against the query.
    /// Returns items with their relevance scores.
    /// External results get a small boost to prioritize the larger catalog.
    /// </summary>
    private static List<(T Item, int Score)> ScoreSearchResults<T>(
        string query,
        List<T> items,
        Func<T, string> titleField,
        Func<T, string?> artistField,
        Func<T, string?> albumField,
        bool isExternal = false)
    {
        return items.Select(item =>
        {
            var title = titleField(item) ?? "";
            var artist = artistField(item) ?? "";
            var album = albumField(item) ?? "";

            // Token-based fuzzy matching: split query and fields into words
            var queryTokens = query.ToLower()
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var fieldText = $"{title} {artist} {album}".ToLower();
            var fieldTokens = fieldText
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (queryTokens.Count == 0) return (item, 0);

            // Count how many query tokens match field tokens (with fuzzy tolerance)
            var matchedTokens = 0;
            foreach (var queryToken in queryTokens)
            {
                // Check if any field token matches this query token
                var hasMatch = fieldTokens.Any(fieldToken =>
                {
                    // Exact match or substring match
                    if (fieldToken.Contains(queryToken) || queryToken.Contains(fieldToken))
                        return true;

                    // Fuzzy match with Levenshtein distance
                    var similarity = FuzzyMatcher.CalculateSimilarity(queryToken, fieldToken);
                    return similarity >= 70; // 70% similarity threshold for individual words
                });

                if (hasMatch) matchedTokens++;
            }

            // Score = percentage of query tokens that matched
            var baseScore = (matchedTokens * 100) / queryTokens.Count;

            // Give external results a small boost (+5 points) to prioritize the larger catalog
            var finalScore = isExternal ? Math.Min(100, baseScore + 5) : baseScore;

            return (item, finalScore);
        }).ToList();
    }

    #endregion

    #region Spotify Playlist Injection

    /// <summary>
    /// Gets tracks for a Spotify playlist by matching missing tracks against external providers
    /// and merging with existing local tracks from Jellyfin.
    /// 
    /// Supports two modes:
    /// 1. Direct Spotify API (new): Uses SpotifyPlaylistFetcher for ordered tracks with ISRC matching
    /// 2. Jellyfin Plugin (legacy): Uses MissingTrack data from Jellyfin Spotify Import plugin
    /// </summary>
    private async Task<IActionResult> GetSpotifyPlaylistTracksAsync(string spotifyPlaylistName, string playlistId)
    {
        try
        {
            // Try ordered cache first (from direct Spotify API mode)
            if (_spotifyApiSettings.Enabled && _spotifyPlaylistFetcher != null)
            {
                var orderedResult = await GetSpotifyPlaylistTracksOrderedAsync(spotifyPlaylistName, playlistId);
                if (orderedResult != null) return orderedResult;
            }
            
            // Fall back to legacy unordered mode
            return await GetSpotifyPlaylistTracksLegacyAsync(spotifyPlaylistName, playlistId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Spotify playlist tracks {PlaylistName}", spotifyPlaylistName);
            return _responseBuilder.CreateError(500, "Failed to get Spotify playlist tracks");
        }
    }
    
    /// <summary>
    /// New mode: Gets playlist tracks with correct ordering using direct Spotify API data.
    /// </summary>
    private async Task<IActionResult?> GetSpotifyPlaylistTracksOrderedAsync(string spotifyPlaylistName, string playlistId)
    {
        // Check Redis cache first for fast serving
        var cacheKey = $"spotify:playlist:items:{spotifyPlaylistName}";
        var cachedItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(cacheKey);
        
        if (cachedItems != null && cachedItems.Count > 0)
        {
            _logger.LogInformation("✅ Loaded {Count} playlist items from Redis cache for {Playlist}", 
                cachedItems.Count, spotifyPlaylistName);
            
            // Log sample item to verify Spotify IDs are present
            if (cachedItems.Count > 0 && cachedItems[0].ContainsKey("ProviderIds"))
            {
                var providerIds = cachedItems[0]["ProviderIds"] as Dictionary<string, object>;
                var hasSpotifyId = providerIds?.ContainsKey("Spotify") ?? false;
                _logger.LogDebug("Sample cached item has Spotify ID: {HasSpotifyId}", hasSpotifyId);
            }
            
            return new JsonResult(new
            {
                Items = cachedItems,
                TotalRecordCount = cachedItems.Count,
                StartIndex = 0
            });
        }
        
        // Check file cache as fallback
        var fileItems = await LoadPlaylistItemsFromFile(spotifyPlaylistName);
        if (fileItems != null && fileItems.Count > 0)
        {
            _logger.LogInformation("✅ Loaded {Count} playlist items from file cache for {Playlist}", 
                fileItems.Count, spotifyPlaylistName);
            
            // Restore to Redis cache
            await _cache.SetAsync(cacheKey, fileItems, TimeSpan.FromHours(24));
            
            return new JsonResult(new
            {
                Items = fileItems,
                TotalRecordCount = fileItems.Count,
                StartIndex = 0
            });
        }
        
        // Check for ordered matched tracks from SpotifyTrackMatchingService
        var orderedCacheKey = $"spotify:matched:ordered:{spotifyPlaylistName}";
        var orderedTracks = await _cache.GetAsync<List<MatchedTrack>>(orderedCacheKey);
        
        if (orderedTracks == null || orderedTracks.Count == 0)
        {
            _logger.LogDebug("No ordered matched tracks in cache for {Playlist}, checking if we can fetch", 
                spotifyPlaylistName);
            return null; // Fall back to legacy mode
        }
        
        _logger.LogDebug("Using {Count} ordered matched tracks for {Playlist}", 
            orderedTracks.Count, spotifyPlaylistName);
        
        // Get existing Jellyfin playlist items (RAW - don't convert!)
        // CRITICAL: Must include UserId parameter or Jellyfin returns empty results
        var userId = _settings.UserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogError("❌ JELLYFIN_USER_ID is NOT configured! Cannot fetch playlist tracks. Set it in .env or admin UI.");
            return null; // Fall back to legacy mode
        }
        
        // Pass through all requested fields from the original request
        var queryString = Request.QueryString.Value ?? "";
        var playlistItemsUrl = $"Playlists/{playlistId}/Items?UserId={userId}";
        
        // Append the original query string (which includes Fields parameter)
        if (!string.IsNullOrEmpty(queryString))
        {
            // Remove the leading ? if present
            queryString = queryString.TrimStart('?');
            playlistItemsUrl = $"{playlistItemsUrl}&{queryString}";
        }
        
        _logger.LogInformation("🔍 Fetching existing tracks from Jellyfin playlist {PlaylistId} with UserId {UserId}", 
            playlistId, userId);
        
        var (existingTracksResponse, statusCode) = await _proxyService.GetJsonAsync(
            playlistItemsUrl, 
            null, 
            Request.Headers);
        
        if (statusCode != 200)
        {
            _logger.LogError("❌ Failed to fetch Jellyfin playlist items: HTTP {StatusCode}. Check JELLYFIN_USER_ID is correct.", statusCode);
            return null;
        }
        
        // Keep raw Jellyfin items - don't convert to Song objects!
        var jellyfinItems = new List<JsonElement>();
        var jellyfinItemsByName = new Dictionary<string, JsonElement>();
        
        if (existingTracksResponse != null && 
            existingTracksResponse.RootElement.TryGetProperty("Items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                jellyfinItems.Add(item);
                
                // Index by title+artist for matching
                var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var artist = "";
                if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                {
                    artist = artistsEl[0].GetString() ?? "";
                }
                else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                {
                    artist = albumArtistEl.GetString() ?? "";
                }
                
                var key = $"{title}|{artist}".ToLowerInvariant();
                if (!jellyfinItemsByName.ContainsKey(key))
                {
                    jellyfinItemsByName[key] = item;
                }
            }
            
            _logger.LogInformation("✅ Found {Count} existing LOCAL tracks in Jellyfin playlist", jellyfinItems.Count);
        }
        else
        {
            _logger.LogWarning("⚠️ No existing tracks found in Jellyfin playlist {PlaylistId} - playlist may be empty", playlistId);
        }
        
        // Get the full playlist from Spotify to know the correct order
        var spotifyTracks = await _spotifyPlaylistFetcher!.GetPlaylistTracksAsync(spotifyPlaylistName);
        if (spotifyTracks.Count == 0)
        {
            _logger.LogWarning("Could not get Spotify playlist tracks for {Playlist}", spotifyPlaylistName);
            return null; // Fall back to legacy
        }
        
        // Build the final track list in correct Spotify order
        var finalItems = new List<Dictionary<string, object?>>();
        var usedJellyfinItems = new HashSet<string>();
        var localUsedCount = 0;
        var externalUsedCount = 0;
        
        _logger.LogInformation("🔍 Building playlist in Spotify order with {SpotifyCount} positions...", spotifyTracks.Count);
        
        foreach (var spotifyTrack in spotifyTracks.OrderBy(t => t.Position))
        {
            // Try to find matching Jellyfin item by fuzzy matching
            JsonElement? matchedJellyfinItem = null;
            string? matchedKey = null;
            double bestScore = 0;
            
            foreach (var kvp in jellyfinItemsByName)
            {
                if (usedJellyfinItems.Contains(kvp.Key)) continue;
                
                var item = kvp.Value;
                var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var artist = "";
                if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                {
                    artist = artistsEl[0].GetString() ?? "";
                }
                
                var titleScore = FuzzyMatcher.CalculateSimilarity(spotifyTrack.Title, title);
                var artistScore = FuzzyMatcher.CalculateSimilarity(spotifyTrack.PrimaryArtist, artist);
                var totalScore = (titleScore * 0.7) + (artistScore * 0.3);
                
                if (totalScore > bestScore && totalScore >= 70)
                {
                    bestScore = totalScore;
                    matchedJellyfinItem = item;
                    matchedKey = kvp.Key;
                }
            }
            
            if (matchedJellyfinItem.HasValue && matchedKey != null)
            {
                // Use the raw Jellyfin item (preserves ALL metadata including MediaSources!)
                var itemDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(matchedJellyfinItem.Value.GetRawText());
                if (itemDict != null)
                {
                    finalItems.Add(itemDict);
                    usedJellyfinItems.Add(matchedKey);
                    localUsedCount++;
                    _logger.LogDebug("✅ Position #{Pos}: '{Title}' → LOCAL (score: {Score:F1}%)", 
                        spotifyTrack.Position, spotifyTrack.Title, bestScore);
                }
            }
            else
            {
                // No local match - try to find external track
                var matched = orderedTracks?.FirstOrDefault(t => t.SpotifyId == spotifyTrack.SpotifyId);
                if (matched != null && matched.MatchedSong != null)
                {
                    // Convert external song to Jellyfin item format
                    var externalItem = _responseBuilder.ConvertSongToJellyfinItem(matched.MatchedSong);
                    
                    // Add Spotify ID to ProviderIds so lyrics can work
                    if (!string.IsNullOrEmpty(spotifyTrack.SpotifyId))
                    {
                        if (!externalItem.ContainsKey("ProviderIds"))
                        {
                            externalItem["ProviderIds"] = new Dictionary<string, string>();
                        }
                        
                        var providerIds = externalItem["ProviderIds"] as Dictionary<string, string>;
                        if (providerIds != null && !providerIds.ContainsKey("Spotify"))
                        {
                            providerIds["Spotify"] = spotifyTrack.SpotifyId;
                        }
                    }
                    
                    finalItems.Add(externalItem);
                    externalUsedCount++;
                    _logger.LogDebug("📥 Position #{Pos}: '{Title}' → EXTERNAL: {Provider}/{Id} (Spotify ID: {SpotifyId})", 
                        spotifyTrack.Position, spotifyTrack.Title,
                        matched.MatchedSong.ExternalProvider, matched.MatchedSong.ExternalId, spotifyTrack.SpotifyId);
                }
                else
                {
                    _logger.LogDebug("❌ Position #{Pos}: '{Title}' → NO MATCH", 
                        spotifyTrack.Position, spotifyTrack.Title);
                }
            }
        }
        
        _logger.LogInformation(
            "🎵 Final playlist '{Playlist}': {Total} tracks ({Local} LOCAL + {External} EXTERNAL)", 
            spotifyPlaylistName, finalItems.Count, localUsedCount, externalUsedCount);
        
        // Save to file cache for persistence across restarts
        await SavePlaylistItemsToFile(spotifyPlaylistName, finalItems);
        
        // Also cache in Redis for fast serving (reuse the same cache key from top of method)
        await _cache.SetAsync(cacheKey, finalItems, TimeSpan.FromHours(24));
        
        // Return raw Jellyfin response format
        return new JsonResult(new
        {
            Items = finalItems,
            TotalRecordCount = finalItems.Count,
            StartIndex = 0
        });
    }
    
    /// <summary>
    /// Legacy mode: Gets playlist tracks without ordering (from Jellyfin Spotify Import plugin).
    /// </summary>
    private async Task<IActionResult> GetSpotifyPlaylistTracksLegacyAsync(string spotifyPlaylistName, string playlistId)
    {
        var cacheKey = $"spotify:matched:{spotifyPlaylistName}";
        var cachedTracks = await _cache.GetAsync<List<Song>>(cacheKey);
        
        if (cachedTracks != null && cachedTracks.Count > 0)
        {
            _logger.LogDebug("Returning {Count} cached matched tracks from Redis for {Playlist}", 
                cachedTracks.Count, spotifyPlaylistName);
            return _responseBuilder.CreateItemsResponse(cachedTracks);
        }
        
        // Try file cache if Redis is empty
        if (cachedTracks == null || cachedTracks.Count == 0)
        {
            cachedTracks = await LoadMatchedTracksFromFile(spotifyPlaylistName);
            if (cachedTracks != null && cachedTracks.Count > 0)
            {
                // Restore to Redis with 1 hour TTL
                await _cache.SetAsync(cacheKey, cachedTracks, TimeSpan.FromHours(1));
                _logger.LogInformation("Loaded {Count} matched tracks from file cache for {Playlist}", 
                    cachedTracks.Count, spotifyPlaylistName);
                return _responseBuilder.CreateItemsResponse(cachedTracks);
            }
        }

        // Get existing Jellyfin playlist items (tracks the plugin already found)
        // CRITICAL: Must include UserId parameter or Jellyfin returns empty results
        var userId = _settings.UserId;
        var playlistItemsUrl = $"Playlists/{playlistId}/Items";
        if (!string.IsNullOrEmpty(userId))
        {
            playlistItemsUrl += $"?UserId={userId}";
        }
        else
        {
            _logger.LogWarning("No UserId configured - may not be able to fetch existing playlist tracks");
        }
        
        var (existingTracksResponse, _) = await _proxyService.GetJsonAsync(
            playlistItemsUrl, 
            null, 
            Request.Headers);
        
        var existingTracks = new List<Song>();
        var existingSpotifyIds = new HashSet<string>();
        
        if (existingTracksResponse != null && 
            existingTracksResponse.RootElement.TryGetProperty("Items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var song = _modelMapper.ParseSong(item);
                existingTracks.Add(song);
                
                // Track Spotify IDs to avoid duplicates
                if (item.TryGetProperty("ProviderIds", out var providerIds) &&
                    providerIds.TryGetProperty("Spotify", out var spotifyId))
                {
                    existingSpotifyIds.Add(spotifyId.GetString() ?? "");
                }
            }
            _logger.LogInformation("Found {Count} existing tracks in Jellyfin playlist", existingTracks.Count);
        }
        else
        {
            _logger.LogWarning("No existing tracks found in Jellyfin playlist - may need UserId parameter");
        }

        var missingTracksKey = $"spotify:missing:{spotifyPlaylistName}";
        var missingTracks = await _cache.GetAsync<List<MissingTrack>>(missingTracksKey);
        
        // Fallback to file cache if Redis is empty
        if (missingTracks == null || missingTracks.Count == 0)
        {
            missingTracks = await LoadMissingTracksFromFile(spotifyPlaylistName);
            
            // If we loaded from file, restore to Redis with no expiration
            if (missingTracks != null && missingTracks.Count > 0)
            {
                await _cache.SetAsync(missingTracksKey, missingTracks, TimeSpan.FromDays(365));
                _logger.LogInformation("Restored {Count} missing tracks from file cache for {Playlist} (no expiration)", 
                    missingTracks.Count, spotifyPlaylistName);
            }
        }
        
        if (missingTracks == null || missingTracks.Count == 0)
        {
            _logger.LogInformation("No missing tracks found for {Playlist}, returning {Count} existing tracks", 
                spotifyPlaylistName, existingTracks.Count);
            return _responseBuilder.CreateItemsResponse(existingTracks);
        }

        _logger.LogInformation("Matching {Count} missing tracks for {Playlist}", 
            missingTracks.Count, spotifyPlaylistName);

        // Match missing tracks sequentially with rate limiting (excluding ones we already have locally)
        var matchedBySpotifyId = new Dictionary<string, Song>();
        var tracksToMatch = missingTracks
            .Where(track => !existingSpotifyIds.Contains(track.SpotifyId))
            .ToList();

        foreach (var track in tracksToMatch)
        {
            try
            {
                // Search with just title and artist for better matching
                var query = $"{track.Title} {track.PrimaryArtist}";
                var results = await _metadataService.SearchSongsAsync(query, limit: 5);
                
                if (results.Count > 0)
                {
                    // Fuzzy match to find best result
                    // Check that ALL artists match (not just some)
                    var bestMatch = results
                        .Select(song => new
                        {
                            Song = song,
                            TitleScore = FuzzyMatcher.CalculateSimilarity(track.Title, song.Title),
                            ArtistScore = FuzzyMatcher.CalculateArtistMatchScore(track.Artists, song.Artist, song.Contributors)
                        })
                        .Select(x => new
                        {
                            x.Song,
                            x.TitleScore,
                            x.ArtistScore,
                            TotalScore = (x.TitleScore * 0.6) + (x.ArtistScore * 0.4) // Weight title more
                        })
                        .OrderByDescending(x => x.TotalScore)
                        .FirstOrDefault();
                    
                    // Only add if match is good enough (>60% combined score)
                    if (bestMatch != null && bestMatch.TotalScore >= 60)
                    {
                        _logger.LogDebug("Matched '{Title}' by {Artist} -> '{MatchTitle}' by {MatchArtist} (score: {Score:F1})",
                            track.Title, track.PrimaryArtist, 
                            bestMatch.Song.Title, bestMatch.Song.Artist, 
                            bestMatch.TotalScore);
                        matchedBySpotifyId[track.SpotifyId] = bestMatch.Song;
                    }
                    else
                    {
                        _logger.LogDebug("No good match for '{Title}' by {Artist} (best score: {Score:F1})",
                            track.Title, track.PrimaryArtist, bestMatch?.TotalScore ?? 0);
                    }
                }
                
                // Rate limiting: small delay between searches to avoid overwhelming the service
                await Task.Delay(100); // 100ms delay = max 10 searches/second
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to match track: {Title} - {Artist}", 
                    track.Title, track.PrimaryArtist);
            }
        }

        // Build final track list based on playlist configuration
        // Local tracks position is configurable per-playlist
        var playlistConfig = _spotifySettings.GetPlaylistByJellyfinId(playlistId);
        var localTracksPosition = playlistConfig?.LocalTracksPosition ?? LocalTracksPosition.First;
        
        var finalTracks = new List<Song>();
        if (localTracksPosition == LocalTracksPosition.First)
        {
            // Local tracks first, external tracks at the end
            finalTracks.AddRange(existingTracks);
            finalTracks.AddRange(matchedBySpotifyId.Values);
        }
        else
        {
            // External tracks first, local tracks at the end
            finalTracks.AddRange(matchedBySpotifyId.Values);
            finalTracks.AddRange(existingTracks);
        }

        await _cache.SetAsync(cacheKey, finalTracks, TimeSpan.FromHours(1));
        
        // Also save to file cache for persistence across restarts
        await SaveMatchedTracksToFile(spotifyPlaylistName, finalTracks);

        _logger.LogInformation("Final playlist: {Total} tracks ({Existing} local + {Matched} external, LocalTracksPosition: {Position})", 
            finalTracks.Count,
            existingTracks.Count,
            matchedBySpotifyId.Count,
            localTracksPosition);

        return _responseBuilder.CreateItemsResponse(finalTracks);
    }

    /// <summary>
    /// Copies an external track to the kept folder when favorited.
    /// </summary>
    private async Task CopyExternalTrackToKeptAsync(string itemId, string provider, string externalId)
    {
        try
        {
            // Check if already favorited (persistent tracking)
            if (await IsTrackFavoritedAsync(itemId))
            {
                _logger.LogInformation("Track already favorited (persistent): {ItemId}", itemId);
                return;
            }

            // Get the song metadata first to build paths
            var song = await _metadataService.GetSongAsync(provider, externalId);
            if (song == null)
            {
                _logger.LogWarning("Could not find song metadata for {ItemId}", itemId);
                return;
            }

            // Build kept folder path: Artist/Album/
            var keptBasePath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var keptArtistPath = Path.Combine(keptBasePath, PathHelper.SanitizeFileName(song.Artist));
            var keptAlbumPath = Path.Combine(keptArtistPath, PathHelper.SanitizeFileName(song.Album));
            
            // Check if track already exists in kept folder
            if (Directory.Exists(keptAlbumPath))
            {
                var sanitizedTitle = PathHelper.SanitizeFileName(song.Title);
                var existingFiles = Directory.GetFiles(keptAlbumPath, $"*{sanitizedTitle}*");
                if (existingFiles.Length > 0)
                {
                    _logger.LogInformation("Track already exists in kept folder: {Path}", existingFiles[0]);
                    // Mark as favorited even if we didn't download it
                    await MarkTrackAsFavoritedAsync(itemId, song);
                    return;
                }
            }

            // Look for the track in cache folder first
            var cacheBasePath = "/tmp/allstarr-cache";
            var cacheArtistPath = Path.Combine(cacheBasePath, PathHelper.SanitizeFileName(song.Artist));
            var cacheAlbumPath = Path.Combine(cacheArtistPath, PathHelper.SanitizeFileName(song.Album));
            
            string? sourceFilePath = null;
            
            if (Directory.Exists(cacheAlbumPath))
            {
                var sanitizedTitle = PathHelper.SanitizeFileName(song.Title);
                var cacheFiles = Directory.GetFiles(cacheAlbumPath, $"*{sanitizedTitle}*");
                if (cacheFiles.Length > 0)
                {
                    sourceFilePath = cacheFiles[0];
                    _logger.LogInformation("Found track in cache folder: {Path}", sourceFilePath);
                }
            }

            // If not in cache, download it first
            if (sourceFilePath == null)
            {
                _logger.LogInformation("Track not in cache, downloading: {ItemId}", itemId);
                try
                {
                    sourceFilePath = await _downloadService.DownloadSongAsync(provider, externalId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download track {ItemId}", itemId);
                    return;
                }
            }

            // Create the kept folder structure
            Directory.CreateDirectory(keptAlbumPath);

            // Copy file to kept folder
            var fileName = Path.GetFileName(sourceFilePath);
            var keptFilePath = Path.Combine(keptAlbumPath, fileName);

            // Double-check in case of race condition (multiple favorite clicks)
            if (System.IO.File.Exists(keptFilePath))
            {
                _logger.LogInformation("Track already exists in kept folder (race condition): {Path}", keptFilePath);
                await MarkTrackAsFavoritedAsync(itemId, song);
                return;
            }

            System.IO.File.Copy(sourceFilePath, keptFilePath, overwrite: false);
            _logger.LogInformation("✓ Copied track to kept folder: {Path}", keptFilePath);
            
            // Also copy cover art if it exists
            var sourceCoverPath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "cover.jpg");
            if (System.IO.File.Exists(sourceCoverPath))
            {
                var keptCoverPath = Path.Combine(keptAlbumPath, "cover.jpg");
                if (!System.IO.File.Exists(keptCoverPath))
                {
                    System.IO.File.Copy(sourceCoverPath, keptCoverPath, overwrite: false);
                    _logger.LogDebug("Copied cover art to kept folder");
                }
            }

            // Mark as favorited in persistent storage
            await MarkTrackAsFavoritedAsync(itemId, song);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying external track {ItemId} to kept folder", itemId);
        }
    }

    /// <summary>
    /// Removes an external track from the kept folder when unfavorited.
    /// </summary>
    private async Task RemoveExternalTrackFromKeptAsync(string itemId, string provider, string externalId)
    {
        try
        {
            // Mark for deletion instead of immediate deletion
            await MarkTrackForDeletionAsync(itemId);
            _logger.LogInformation("✓ Marked track for deletion: {ItemId}", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking external track {ItemId} for deletion", itemId);
        }
    }

    #region Persistent Favorites Tracking

    private readonly string _favoritesFilePath = "/app/cache/favorites.json";

    /// <summary>
    /// Checks if a track is already favorited (persistent across restarts).
    /// </summary>
    private async Task<bool> IsTrackFavoritedAsync(string itemId)
    {
        try
        {
            if (!System.IO.File.Exists(_favoritesFilePath))
                return false;

            var json = await System.IO.File.ReadAllTextAsync(_favoritesFilePath);
            var favorites = JsonSerializer.Deserialize<Dictionary<string, FavoriteTrackInfo>>(json) ?? new();
            
            return favorites.ContainsKey(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check favorite status for {ItemId}", itemId);
            return false;
        }
    }

    /// <summary>
    /// Marks a track as favorited in persistent storage.
    /// </summary>
    private async Task MarkTrackAsFavoritedAsync(string itemId, Song song)
    {
        try
        {
            var favorites = new Dictionary<string, FavoriteTrackInfo>();
            
            if (System.IO.File.Exists(_favoritesFilePath))
            {
                var json = await System.IO.File.ReadAllTextAsync(_favoritesFilePath);
                favorites = JsonSerializer.Deserialize<Dictionary<string, FavoriteTrackInfo>>(json) ?? new();
            }

            favorites[itemId] = new FavoriteTrackInfo
            {
                ItemId = itemId,
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                FavoritedAt = DateTime.UtcNow
            };

            // Ensure cache directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(_favoritesFilePath)!);
            
            var updatedJson = JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(_favoritesFilePath, updatedJson);
            
            _logger.LogDebug("Marked track as favorited: {ItemId}", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark track as favorited: {ItemId}", itemId);
        }
    }

    /// <summary>
    /// Removes a track from persistent favorites storage.
    /// </summary>
    private async Task UnmarkTrackAsFavoritedAsync(string itemId)
    {
        try
        {
            if (!System.IO.File.Exists(_favoritesFilePath))
                return;

            var json = await System.IO.File.ReadAllTextAsync(_favoritesFilePath);
            var favorites = JsonSerializer.Deserialize<Dictionary<string, FavoriteTrackInfo>>(json) ?? new();
            
            if (favorites.Remove(itemId))
            {
                var updatedJson = JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(_favoritesFilePath, updatedJson);
                _logger.LogDebug("Removed track from favorites: {ItemId}", itemId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove track from favorites: {ItemId}", itemId);
        }
    }

    /// <summary>
    /// Marks a track for deletion (delayed deletion for safety).
    /// </summary>
    private async Task MarkTrackForDeletionAsync(string itemId)
    {
        try
        {
            var deletionFilePath = "/app/cache/pending_deletions.json";
            var pendingDeletions = new Dictionary<string, DateTime>();
            
            if (System.IO.File.Exists(deletionFilePath))
            {
                var json = await System.IO.File.ReadAllTextAsync(deletionFilePath);
                pendingDeletions = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? new();
            }

            // Mark for deletion 24 hours from now
            pendingDeletions[itemId] = DateTime.UtcNow.AddHours(24);

            // Ensure cache directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(deletionFilePath)!);
            
            var updatedJson = JsonSerializer.Serialize(pendingDeletions, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(deletionFilePath, updatedJson);
            
            // Also remove from favorites immediately
            await UnmarkTrackAsFavoritedAsync(itemId);
            
            _logger.LogDebug("Marked track for deletion in 24 hours: {ItemId}", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark track for deletion: {ItemId}", itemId);
        }
    }

    /// <summary>
    /// Information about a favorited track for persistent storage.
    /// </summary>
    private class FavoriteTrackInfo
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public DateTime FavoritedAt { get; set; }
    }

    /// <summary>
    /// Processes pending deletions (called by cleanup service).
    /// </summary>
    public async Task ProcessPendingDeletionsAsync()
    {
        try
        {
            var deletionFilePath = "/app/cache/pending_deletions.json";
            if (!System.IO.File.Exists(deletionFilePath))
                return;

            var json = await System.IO.File.ReadAllTextAsync(deletionFilePath);
            var pendingDeletions = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? new();
            
            var now = DateTime.UtcNow;
            var toDelete = pendingDeletions.Where(kvp => kvp.Value <= now).ToList();
            var remaining = pendingDeletions.Where(kvp => kvp.Value > now).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            foreach (var (itemId, _) in toDelete)
            {
                await ActuallyDeleteTrackAsync(itemId);
            }

            if (toDelete.Count > 0)
            {
                // Update pending deletions file
                var updatedJson = JsonSerializer.Serialize(remaining, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(deletionFilePath, updatedJson);
                
                _logger.LogInformation("Processed {Count} pending deletions", toDelete.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pending deletions");
        }
    }

    /// <summary>
    /// Actually deletes a track from the kept folder.
    /// </summary>
    private async Task ActuallyDeleteTrackAsync(string itemId)
    {
        try
        {
            var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);
            if (!isExternal) return;

            var song = await _metadataService.GetSongAsync(provider!, externalId!);
            if (song == null) return;

            var keptBasePath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var keptArtistPath = Path.Combine(keptBasePath, PathHelper.SanitizeFileName(song.Artist));
            var keptAlbumPath = Path.Combine(keptArtistPath, PathHelper.SanitizeFileName(song.Album));
            
            if (!Directory.Exists(keptAlbumPath)) return;

            var sanitizedTitle = PathHelper.SanitizeFileName(song.Title);
            var trackFiles = Directory.GetFiles(keptAlbumPath, $"*{sanitizedTitle}*");
            
            foreach (var trackFile in trackFiles)
            {
                System.IO.File.Delete(trackFile);
                _logger.LogInformation("✓ Deleted track from kept folder: {Path}", trackFile);
            }

            // Clean up empty directories
            if (Directory.GetFiles(keptAlbumPath).Length == 0 && Directory.GetDirectories(keptAlbumPath).Length == 0)
            {
                Directory.Delete(keptAlbumPath);
                
                if (Directory.Exists(keptArtistPath) && 
                    Directory.GetFiles(keptArtistPath).Length == 0 && 
                    Directory.GetDirectories(keptArtistPath).Length == 0)
                {
                    Directory.Delete(keptArtistPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete track {ItemId}", itemId);
        }
    }

    #endregion

    /// <summary>
    /// Loads missing tracks from file cache as fallback when Redis is empty.
    /// </summary>
    private async Task<List<allstarr.Models.Spotify.MissingTrack>?> LoadMissingTracksFromFile(string playlistName)
    {
        try
        {
            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine("/app/cache/spotify", $"{safeName}_missing.json");
            
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogDebug("No file cache found for {Playlist} at {Path}", playlistName, filePath);
                return null;
            }
            
            // No expiration check - cache persists until next Jellyfin job generates new file
            var fileAge = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(filePath);
            _logger.LogDebug("File cache for {Playlist} age: {Age:F1}h (no expiration)", playlistName, fileAge.TotalHours);
            
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            var tracks = JsonSerializer.Deserialize<List<allstarr.Models.Spotify.MissingTrack>>(json);
            
            _logger.LogInformation("Loaded {Count} missing tracks from file cache for {Playlist} (age: {Age:F1}h)", 
                tracks?.Count ?? 0, playlistName, fileAge.TotalHours);
            
            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load missing tracks from file for {Playlist}", playlistName);
            return null;
        }
    }

    /// <summary>
    /// Loads matched/combined tracks from file cache as fallback when Redis is empty.
    /// </summary>
    private async Task<List<Song>?> LoadMatchedTracksFromFile(string playlistName)
    {
        try
        {
            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine("/app/cache/spotify", $"{safeName}_matched.json");
            
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogDebug("No matched tracks file cache found for {Playlist} at {Path}", playlistName, filePath);
                return null;
            }
            
            var fileAge = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(filePath);
            
            // Check if cache is too old (more than 24 hours)
            if (fileAge.TotalHours > 24)
            {
                _logger.LogInformation("Matched tracks file cache for {Playlist} is too old ({Age:F1}h), will rebuild", 
                    playlistName, fileAge.TotalHours);
                return null;
            }
            
            _logger.LogDebug("Matched tracks file cache for {Playlist} age: {Age:F1}h", playlistName, fileAge.TotalHours);
            
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            var tracks = JsonSerializer.Deserialize<List<Song>>(json);
            
            _logger.LogInformation("Loaded {Count} matched tracks from file cache for {Playlist} (age: {Age:F1}h)", 
                tracks?.Count ?? 0, playlistName, fileAge.TotalHours);
            
            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load matched tracks from file for {Playlist}", playlistName);
            return null;
        }
    }

    /// <summary>
    /// Saves matched/combined tracks to file cache for persistence across restarts.
    /// </summary>
    private async Task SaveMatchedTracksToFile(string playlistName, List<Song> tracks)
    {
        try
        {
            var cacheDir = "/app/cache/spotify";
            Directory.CreateDirectory(cacheDir);
            
            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(cacheDir, $"{safeName}_matched.json");
            
            var json = JsonSerializer.Serialize(tracks, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);
            
            _logger.LogInformation("Saved {Count} matched tracks to file cache for {Playlist}", 
                tracks.Count, playlistName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save matched tracks to file for {Playlist}", playlistName);
        }
    }

    /// <summary>
    /// Saves playlist items (raw Jellyfin JSON) to file cache for persistence across restarts.
    /// </summary>
    private async Task SavePlaylistItemsToFile(string playlistName, List<Dictionary<string, object?>> items)
    {
        try
        {
            var cacheDir = "/app/cache/spotify";
            Directory.CreateDirectory(cacheDir);
            
            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(cacheDir, $"{safeName}_items.json");
            
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);
            
            _logger.LogInformation("💾 Saved {Count} playlist items to file cache for {Playlist}", 
                items.Count, playlistName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist items to file for {Playlist}", playlistName);
        }
    }

    /// <summary>
    /// Loads playlist items (raw Jellyfin JSON) from file cache.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>?> LoadPlaylistItemsFromFile(string playlistName)
    {
        try
        {
            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine("/app/cache/spotify", $"{safeName}_items.json");
            
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogDebug("No playlist items file cache found for {Playlist} at {Path}", playlistName, filePath);
                return null;
            }
            
            var fileAge = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(filePath);
            
            // Check if cache is too old (more than 24 hours)
            if (fileAge.TotalHours > 24)
            {
                _logger.LogInformation("Playlist items file cache for {Playlist} is too old ({Age:F1}h), will rebuild", 
                    playlistName, fileAge.TotalHours);
                return null;
            }
            
            _logger.LogDebug("Playlist items file cache for {Playlist} age: {Age:F1}h", playlistName, fileAge.TotalHours);
            
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            var items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json);
            
            _logger.LogInformation("💿 Loaded {Count} playlist items from file cache for {Playlist} (age: {Age:F1}h)", 
                items?.Count ?? 0, playlistName, fileAge.TotalHours);
            
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load playlist items from file for {Playlist}", playlistName);
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Calculates artist match score ensuring ALL artists are present.
    /// Penalizes if artist counts don't match or if any artist is missing.
    /// </summary>
    private static double CalculateArtistMatchScore(List<string> spotifyArtists, string songMainArtist, List<string> songContributors)
    {
        if (spotifyArtists.Count == 0 || string.IsNullOrEmpty(songMainArtist))
            return 0;
        
        // Build list of all song artists (main + contributors)
        var allSongArtists = new List<string> { songMainArtist };
        allSongArtists.AddRange(songContributors);
        
        // If artist counts differ significantly, penalize
        var countDiff = Math.Abs(spotifyArtists.Count - allSongArtists.Count);
        if (countDiff > 1) // Allow 1 artist difference (sometimes features are listed differently)
            return 0;
        
        // Check that each Spotify artist has a good match in song artists
        var spotifyScores = new List<double>();
        foreach (var spotifyArtist in spotifyArtists)
        {
            var bestMatch = allSongArtists.Max(songArtist => 
                FuzzyMatcher.CalculateSimilarity(spotifyArtist, songArtist));
            spotifyScores.Add(bestMatch);
        }
        
        // Check that each song artist has a good match in Spotify artists
        var songScores = new List<double>();
        foreach (var songArtist in allSongArtists)
        {
            var bestMatch = spotifyArtists.Max(spotifyArtist => 
                FuzzyMatcher.CalculateSimilarity(songArtist, spotifyArtist));
            songScores.Add(bestMatch);
        }
        
        // Average all scores - this ensures ALL artists must match well
        var allScores = spotifyScores.Concat(songScores);
        var avgScore = allScores.Average();
        
        // Penalize if any individual artist match is poor (< 70)
        var minScore = allScores.Min();
        if (minScore < 70)
            avgScore *= 0.7; // 30% penalty for poor individual match
        
        return avgScore;
    }

    /// <summary>
    /// Extracts device information from Authorization header.
    /// </summary>
    private (string? deviceId, string? client, string? device, string? version) ExtractDeviceInfo(IHeaderDictionary headers)
    {
        string? deviceId = null;
        string? client = null;
        string? device = null;
        string? version = null;
        
        // Check X-Emby-Authorization FIRST (most Jellyfin clients use this)
        // Then fall back to Authorization header
        string? authStr = null;
        if (headers.TryGetValue("X-Emby-Authorization", out var embyAuthHeader))
        {
            authStr = embyAuthHeader.ToString();
        }
        else if (headers.TryGetValue("Authorization", out var authHeader))
        {
            authStr = authHeader.ToString();
        }
        
        if (!string.IsNullOrEmpty(authStr))
        {
            // Parse: MediaBrowser Client="...", Device="...", DeviceId="...", Version="..."
            var parts = authStr.Replace("MediaBrowser ", "").Split(',');
            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim();
                    var value = kv[1].Trim('"');
                    if (key == "DeviceId") deviceId = value;
                    else if (key == "Client") client = value;
                    else if (key == "Device") device = value;
                    else if (key == "Version") version = value;
                }
            }
        }
        
        return (deviceId, client, device, version);
    }
    
    /// <summary>
    /// Generates a deterministic UUID (v5) from a string.
    /// This allows us to create consistent UUIDs for external track IDs.
    /// </summary>
    private string GenerateUuidFromString(string input)
    {
        // Use MD5 hash to generate a deterministic UUID
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        
        // Convert to UUID format (version 5, namespace-based)
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // Version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // Variant
        
        var guid = new Guid(hash);
        return guid.ToString();
    }
    
    /// <summary>
    /// Finds the Spotify ID for an external track by searching through all playlist matched tracks caches.
    /// This allows us to get Spotify lyrics for external tracks that were matched from Spotify playlists.
    /// </summary>
    private async Task<string?> FindSpotifyIdForExternalTrackAsync(Song externalSong)
    {
        try
        {
            // Get all configured playlists
            var playlists = _spotifySettings.Playlists;
            
            // Search through each playlist's matched tracks cache
            foreach (var playlist in playlists)
            {
                var cacheKey = $"spotify:matched:ordered:{playlist.Name}";
                var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(cacheKey);
                
                if (matchedTracks == null || matchedTracks.Count == 0)
                    continue;
                
                // Look for a match by external ID
                var match = matchedTracks.FirstOrDefault(t => 
                    t.MatchedSong != null && 
                    t.MatchedSong.ExternalProvider == externalSong.ExternalProvider &&
                    t.MatchedSong.ExternalId == externalSong.ExternalId);
                
                if (match != null && !string.IsNullOrEmpty(match.SpotifyId))
                {
                    _logger.LogDebug("Found Spotify ID {SpotifyId} for {Provider}/{ExternalId} in playlist {Playlist}", 
                        match.SpotifyId, externalSong.ExternalProvider, externalSong.ExternalId, playlist.Name);
                    return match.SpotifyId;
                }
            }
            
            _logger.LogDebug("No Spotify ID found for external track {Provider}/{ExternalId}", 
                externalSong.ExternalProvider, externalSong.ExternalId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error finding Spotify ID for external track");
            return null;
        }
    }
}

// force rebuild Sun Jan 25 13:22:47 EST 2026
