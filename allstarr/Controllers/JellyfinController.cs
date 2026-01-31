using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Subsonic;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using allstarr.Services.Lyrics;

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
    private readonly IMusicMetadataService _metadataService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly JellyfinResponseBuilder _responseBuilder;
    private readonly JellyfinModelMapper _modelMapper;
    private readonly JellyfinProxyService _proxyService;
    private readonly PlaylistSyncService? _playlistSyncService;
    private readonly RedisCacheService _cache;
    private readonly ILogger<JellyfinController> _logger;

    public JellyfinController(
        IOptions<JellyfinSettings> settings,
        IOptions<SpotifyImportSettings> spotifySettings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        IDownloadService downloadService,
        JellyfinResponseBuilder responseBuilder,
        JellyfinModelMapper modelMapper,
        JellyfinProxyService proxyService,
        RedisCacheService cache,
        ILogger<JellyfinController> logger,
        PlaylistSyncService? playlistSyncService = null)
    {
        _settings = settings.Value;
        _spotifySettings = spotifySettings.Value;
        _metadataService = metadataService;
        _localLibraryService = localLibraryService;
        _downloadService = downloadService;
        _responseBuilder = responseBuilder;
        _modelMapper = modelMapper;
        _proxyService = proxyService;
        _playlistSyncService = playlistSyncService;
        _cache = cache;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.Url))
        {
            throw new InvalidOperationException("JELLYFIN_URL environment variable is not set");
        }
        
        // Log Spotify Import configuration on first controller instantiation
        _logger.LogInformation("========================================");
        _logger.LogInformation("Spotify Import Configuration:");
        _logger.LogInformation("  Enabled: {Enabled}", _spotifySettings.Enabled);
        _logger.LogInformation("  Sync Time: {Hour}:{Minute:D2}", _spotifySettings.SyncStartHour, _spotifySettings.SyncStartMinute);
        _logger.LogInformation("  Sync Window: {Hours} hours", _spotifySettings.SyncWindowHours);
        _logger.LogInformation("  Configured Playlists: {Count}", _spotifySettings.Playlists.Count);
        foreach (var playlist in _spotifySettings.Playlists)
        {
            _logger.LogInformation("    - {Name} (SpotifyName: {SpotifyName}, Enabled: {Enabled})", 
                playlist.Name, playlist.SpotifyName, playlist.Enabled);
        }
        _logger.LogInformation("========================================");
    }

    #region Search

    /// <summary>
    /// Searches local Jellyfin library and external providers.
    /// Dedupes artists, combines songs/albums. Works with /Items and /Users/{userId}/Items.
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
            if (Request.QueryString.HasValue)
            {
                endpoint = $"{endpoint}{Request.QueryString.Value}";
            }
            
            var browseResult = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

            if (browseResult == null)
            {
                _logger.LogInformation("Jellyfin returned null - likely 401 Unauthorized, returning 401 to client");
                return Unauthorized(new { error = "Authentication required" });
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
        var externalTask = _metadataService.SearchAllAsync(cleanQuery, limit, limit, limit);

        var playlistTask = _settings.EnableExternalPlaylists
            ? _metadataService.SearchPlaylistsAsync(cleanQuery, limit)
            : Task.FromResult(new List<ExternalPlaylist>());

        await Task.WhenAll(jellyfinTask, externalTask, playlistTask);

        var jellyfinResult = await jellyfinTask;
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

        // Score and filter Jellyfin results by relevance
        var scoredLocalSongs = ScoreSearchResults(cleanQuery, localSongs, s => s.Title, s => s.Artist, s => s.Album, isExternal: false);
        var scoredLocalAlbums = ScoreSearchResults(cleanQuery, localAlbums, a => a.Title, a => a.Artist, _ => null, isExternal: false);
        var scoredLocalArtists = ScoreSearchResults(cleanQuery, localArtists, a => a.Name, _ => null, _ => null, isExternal: false);

        // Score external results with a small boost
        var scoredExternalSongs = ScoreSearchResults(cleanQuery, externalResult.Songs, s => s.Title, s => s.Artist, s => s.Album, isExternal: true);
        var scoredExternalAlbums = ScoreSearchResults(cleanQuery, externalResult.Albums, a => a.Title, a => a.Artist, _ => null, isExternal: true);
        var scoredExternalArtists = ScoreSearchResults(cleanQuery, externalResult.Artists, a => a.Name, _ => null, _ => null, isExternal: true);

        // Merge and sort by score (no filtering - just reorder by relevance)
        var allSongs = scoredLocalSongs.Concat(scoredExternalSongs)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();

        var allAlbums = scoredLocalAlbums.Concat(scoredExternalAlbums)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();

        // Dedupe artists by name, keeping highest scored version
        var artistScores = scoredLocalArtists.Concat(scoredExternalArtists)
            .GroupBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();

        // Convert to Jellyfin format
        var mergedSongs = allSongs.Select(s => _responseBuilder.ConvertSongToJellyfinItem(s)).ToList();
        var mergedAlbums = allAlbums.Select(a => _responseBuilder.ConvertAlbumToJellyfinItem(a)).ToList();
        var mergedArtists = artistScores.Select(a => _responseBuilder.ConvertArtistToJellyfinItem(a)).ToList();

        // Add playlists (score them too)
        if (playlistResult.Count > 0)
        {
            var scoredPlaylists = playlistResult
                .Select(p => new { Playlist = p, Score = FuzzyMatcher.CalculateSimilarity(cleanQuery, p.Name) })
                .OrderByDescending(x => x.Score)
                .Select(x => _responseBuilder.ConvertPlaylistToJellyfinItem(x.Playlist))
                .ToList();
            
            mergedAlbums.AddRange(scoredPlaylists);
        }

        _logger.LogInformation("Scored and filtered results: Songs={Songs}, Albums={Albums}, Artists={Artists}",
            mergedSongs.Count, mergedAlbums.Count, mergedArtists.Count);

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
        var result = await _proxyService.GetItemsAsync(
            parentId: parentId,
            includeItemTypes: ParseItemTypes(includeItemTypes),
            sortBy: sortBy,
            limit: limit,
            startIndex: startIndex,
            clientHeaders: Request.Headers);

        if (result == null)
        {
            return _responseBuilder.CreateError(404, "Parent not found");
        }

        return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
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

        var jellyfinResult = await jellyfinTask;
        var externalResult = await externalTask;

        var (localSongs, localAlbums, localArtists) = _modelMapper.ParseItemsResponse(jellyfinResult);

        // Merge and convert to search hints format
        var allSongs = localSongs.Concat(externalResult.Songs).Take(limit).ToList();
        var allAlbums = localAlbums.Concat(externalResult.Albums).Take(limit).ToList();

        // Dedupe artists by name
        var artistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allArtists = new List<Artist>();
        foreach (var artist in localArtists.Concat(externalResult.Artists))
        {
            if (artistNames.Add(artist.Name))
            {
                allArtists.Add(artist);
            }
        }

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
        var result = await _proxyService.GetItemAsync(itemId, Request.Headers);
        if (result == null)
        {
            return _responseBuilder.CreateError(404, "Item not found");
        }

        return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
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

        // Check if asking for audio (album tracks)
        if (itemTypes?.Contains("Audio") == true)
        {
            var album = await _metadataService.GetAlbumAsync(provider, externalId);
            if (album == null)
            {
                return _responseBuilder.CreateError(404, "Album not found");
            }

            return _responseBuilder.CreateItemsResponse(album.Songs);
        }

        // Otherwise assume it's artist albums
        var albums = await _metadataService.GetArtistAlbumsAsync(provider, externalId);
        var artist = await _metadataService.GetArtistAsync(provider, externalId);

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

            var jellyfinResult = await jellyfinTask;
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

            // Merge and deduplicate by name
            var artistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mergedArtists = new List<Artist>();

            foreach (var artist in localArtists)
            {
                if (artistNames.Add(artist.Name))
                {
                    mergedArtists.Add(artist);
                }
            }

            foreach (var artist in externalArtists)
            {
                if (artistNames.Add(artist.Name))
                {
                    mergedArtists.Add(artist);
                }
            }

            _logger.LogInformation("Returning {Count} merged artists", mergedArtists.Count);

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
        var result = await _proxyService.GetArtistsAsync(searchTerm, limit, startIndex, Request.Headers);

        if (result == null)
        {
            return new JsonResult(new Dictionary<string, object>
            {
                ["Items"] = Array.Empty<object>(),
                ["TotalRecordCount"] = 0,
                ["StartIndex"] = startIndex
            });
        }

        return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
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
        var jellyfinArtist = await _proxyService.GetArtistAsync(artistIdOrName, Request.Headers);
        if (jellyfinArtist == null)
        {
            return _responseBuilder.CreateError(404, "Artist not found");
        }

        var artistData = _modelMapper.ParseArtist(jellyfinArtist.RootElement);
        var artistName = artistData.Name;
        var localArtistId = artistData.Id;

        // Get local albums
        var localAlbumsResult = await _proxyService.GetItemsAsync(
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
    /// Universal audio endpoint that redirects to the stream endpoint.
    /// </summary>
    [HttpGet("Audio/{itemId}/universal")]
    public Task<IActionResult> UniversalAudio(string itemId)
    {
        return StreamAudio(itemId);
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
            // Redirect to Jellyfin directly for local content images
            var queryString = new List<string>();
            if (maxWidth.HasValue) queryString.Add($"maxWidth={maxWidth.Value}");
            if (maxHeight.HasValue) queryString.Add($"maxHeight={maxHeight.Value}");
            
            var path = $"Items/{itemId}/Images/{imageType}";
            if (imageIndex > 0)
            {
                path = $"Items/{itemId}/Images/{imageType}/{imageIndex}";
            }
            
            if (queryString.Any())
            {
                path = $"{path}?{string.Join("&", queryString)}";
            }
            
            var jellyfinUrl = $"{_settings.Url?.TrimEnd('/')}/{path}";
            return Redirect(jellyfinUrl);
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
            return NotFound();
        }

        // Fetch and return the image using the proxy service's HttpClient
        try
        {
            var response = await _proxyService.HttpClient.GetAsync(coverUrl);
            if (!response.IsSuccessStatusCode)
            {
                return NotFound();
            }

            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            return File(imageBytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch cover art from {Url}", coverUrl);
            return NotFound();
        }
    }

    #endregion

    #region Lyrics

    /// <summary>
    /// Gets lyrics for an item.
    /// </summary>
    [HttpGet("Audio/{itemId}/Lyrics")]
    [HttpGet("Items/{itemId}/Lyrics")]
    public async Task<IActionResult> GetLyrics(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return NotFound();
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

        Song? song = null;
        
        if (isExternal)
        {
            song = await _metadataService.GetSongAsync(provider!, externalId!);
        }
        else
        {
            // For local songs, get metadata from Jellyfin
            var item = await _proxyService.GetItemAsync(itemId, Request.Headers);
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
            }
        }

        if (song == null)
        {
            return NotFound(new { error = "Song not found" });
        }

        // Try to get lyrics from LRCLIB
        var lyricsService = HttpContext.RequestServices.GetService<LrclibService>();
        if (lyricsService == null)
        {
            return NotFound(new { error = "Lyrics service not available" });
        }

        var lyrics = await lyricsService.GetLyricsAsync(
            song.Title,
            song.Artist ?? "",
            song.Album ?? "",
            song.Duration ?? 0);

        if (lyrics == null)
        {
            return NotFound(new { error = "Lyrics not found" });
        }

        // Prefer synced lyrics, fall back to plain
        var lyricsText = lyrics.SyncedLyrics ?? lyrics.PlainLyrics ?? "";
        var isSynced = !string.IsNullOrEmpty(lyrics.SyncedLyrics);

        // Parse LRC format into individual lines for Jellyfin
        var lyricLines = new List<object>();
        
        if (isSynced && !string.IsNullOrEmpty(lyrics.SyncedLyrics))
        {
            // Parse LRC format: [mm:ss.xx] text
            var lines = lyrics.SyncedLyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
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
                    
                    lyricLines.Add(new
                    {
                        Start = ticks,
                        Text = text
                    });
                }
            }
        }
        else
        {
            // Plain lyrics - return as single block
            lyricLines.Add(new
            {
                Start = (long?)null,
                Text = lyricsText
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

        return Ok(response);
    }

    #endregion

    #region Favorites

    /// <summary>
    /// Marks an item as favorite. For playlists, triggers a full download.
    /// </summary>
    [HttpPost("Users/{userId}/FavoriteItems/{itemId}")]
    public async Task<IActionResult> MarkFavorite(string userId, string itemId)
    {
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

            return Ok(new { IsFavorite = true });
        }

        // Check if this is an external song/album
        var (isExternal, _, _) = _localLibraryService.ParseSongId(itemId);
        if (isExternal)
        {
            // External items don't exist in Jellyfin, so we can't favorite them there
            // Just return success - the client will show it as favorited
            _logger.LogDebug("Favoriting external item {ItemId} (not synced to Jellyfin)", itemId);
            return Ok(new { IsFavorite = true });
        }

        // For local Jellyfin items, proxy the request through
        var endpoint = $"Users/{userId}/FavoriteItems/{itemId}";
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.Url?.TrimEnd('/')}/{endpoint}");
            
            // Forward client authentication
            if (Request.Headers.TryGetValue("X-Emby-Authorization", out var embyAuth))
            {
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", embyAuth.ToString());
            }
            else if (Request.Headers.TryGetValue("Authorization", out var auth))
            {
                request.Headers.TryAddWithoutValidation("Authorization", auth.ToString());
            }
            
            var response = await _proxyService.HttpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return Ok(new { IsFavorite = true });
            }
            
            _logger.LogWarning("Failed to favorite item in Jellyfin: {StatusCode}", response.StatusCode);
            return _responseBuilder.CreateError((int)response.StatusCode, "Failed to mark favorite");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error favoriting item {ItemId}", itemId);
            return _responseBuilder.CreateError(500, "Failed to mark favorite");
        }
    }

    /// <summary>
    /// Removes an item from favorites.
    /// </summary>
    [HttpDelete("Users/{userId}/FavoriteItems/{itemId}")]
    public async Task<IActionResult> UnmarkFavorite(string userId, string itemId)
    {
        // External items can't be unfavorited
        var (isExternal, _, _) = _localLibraryService.ParseSongId(itemId);
        if (isExternal || PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            return Ok(new { IsFavorite = false });
        }

        // Proxy to Jellyfin to unfavorite
        var url = $"Users/{userId}/FavoriteItems/{itemId}";
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_settings.Url?.TrimEnd('/')}/{url}");
            
            // Forward client authentication
            if (Request.Headers.TryGetValue("X-Emby-Authorization", out var embyAuth))
            {
                request.Headers.TryAddWithoutValidation("X-Emby-Authorization", embyAuth.ToString());
            }
            else if (Request.Headers.TryGetValue("Authorization", out var auth))
            {
                request.Headers.TryAddWithoutValidation("Authorization", auth.ToString());
            }
            
            var response = await _proxyService.HttpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                return Ok(new { IsFavorite = false });
            }
            
            return _responseBuilder.CreateError(500, "Failed to unfavorite item");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unfavoriting item {ItemId}", itemId);
            return _responseBuilder.CreateError(500, "Failed to unfavorite item");
        }
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

            // Only check for Spotify playlists if the feature is enabled
            _logger.LogInformation("Spotify Import Enabled: {Enabled}, Playlist starts with ext-: {IsExternal}", 
                _spotifySettings.Enabled, playlistId.StartsWith("ext-"));
            
            if (_spotifySettings.Enabled && !playlistId.StartsWith("ext-"))
            {
                // Get playlist info from Jellyfin to check the name
                _logger.LogInformation("Fetching playlist info from Jellyfin for ID: {PlaylistId}", playlistId);
                var playlistInfo = await _proxyService.GetJsonAsync($"Items/{playlistId}", null, Request.Headers);
                
                if (playlistInfo != null && playlistInfo.RootElement.TryGetProperty("Name", out var nameElement))
                {
                    var playlistName = nameElement.GetString() ?? "";
                    _logger.LogInformation("Jellyfin playlist name: '{PlaylistName}'", playlistName);
                    
                    // Check if this matches any configured Spotify playlists
                    var matchingConfig = _spotifySettings.Playlists
                        .FirstOrDefault(p => p.Enabled && p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));
                    
                    if (matchingConfig != null)
                    {
                        _logger.LogInformation("✓ MATCHED! Intercepting Spotify playlist: {PlaylistName}", playlistName);
                        return await GetSpotifyPlaylistTracksAsync(matchingConfig.SpotifyName);
                    }
                    else
                    {
                        _logger.LogInformation("✗ No match found for playlist '{PlaylistName}'", playlistName);
                        _logger.LogInformation("Configured playlists: {Playlists}", 
                            string.Join(", ", _spotifySettings.Playlists.Select(p => $"'{p.Name}' (enabled={p.Enabled})")));
                    }
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
            var result = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
            if (result == null)
            {
                return _responseBuilder.CreateError(404, "Playlist not found");
            }
            
            return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
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
            
            // Forward to Jellyfin server with client headers
            var result = await _proxyService.PostJsonAsync("Users/AuthenticateByName", body, Request.Headers);
            
            if (result == null)
            {
                _logger.LogWarning("Authentication failed - no response from Jellyfin");
                return Unauthorized(new { error = "Authentication failed" });
            }
            
            _logger.LogInformation("Authentication successful");
            return Content(result.RootElement.GetRawText(), "application/json");
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

        var result = await _proxyService.GetJsonAsync(endpoint, queryParams, Request.Headers);
        
        if (result == null)
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                Items = Array.Empty<object>(),
                TotalRecordCount = 0
            });
        }

        return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
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

        var result = await _proxyService.GetJsonAsync($"Songs/{itemId}/InstantMix", queryParams, Request.Headers);
        
        if (result == null)
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                Items = Array.Empty<object>(),
                TotalRecordCount = 0
            });
        }

        return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
    }

    #endregion

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
    /// Intercepts playlist items requests to inject Spotify playlist tracks.
    /// </summary>
    [HttpGet("Playlists/{playlistId}/Items", Order = 1)]
    [HttpGet("playlists/{playlistId}/items", Order = 1)]
    public async Task<IActionResult> GetPlaylistItems(string playlistId)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("=== GetPlaylistItems INTERCEPTED ===");
        _logger.LogInformation("PlaylistId: {PlaylistId}", playlistId);
        _logger.LogInformation("Spotify Import Enabled: {Enabled}", _spotifySettings.Enabled);
        _logger.LogInformation("Configured Playlists: {Count}", _spotifySettings.Playlists.Count);
        foreach (var p in _spotifySettings.Playlists)
        {
            _logger.LogInformation("  - {Name} (SpotifyName: {SpotifyName}, Enabled: {Enabled})", 
                p.Name, p.SpotifyName, p.Enabled);
        }
        _logger.LogInformation("========================================");
        return await GetPlaylistTracks(playlistId);
    }

    /// <summary>
    /// Catch-all endpoint that proxies unhandled requests to Jellyfin transparently.
    /// This route has the lowest priority and should only match requests that don't have SearchTerm.
    /// </summary>
    [HttpGet("{**path}", Order = 100)]
    [HttpPost("{**path}", Order = 100)]
    public async Task<IActionResult> ProxyRequest(string path)
    {
        // Log all playlist requests for debugging
        if (path.Contains("playlist", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("=== PLAYLIST REQUEST === Path: {Path}, SpotifyEnabled: {Enabled}, PlaylistCount: {Count}", 
                path, _spotifySettings.Enabled, _spotifySettings.Playlists.Count);
            
            foreach (var p in _spotifySettings.Playlists)
            {
                _logger.LogInformation("  Configured playlist: {Name} (SpotifyName: {SpotifyName}, Enabled: {Enabled})", 
                    p.Name, p.SpotifyName, p.Enabled);
            }
        }
        
        // Intercept Spotify playlist requests
        if (_spotifySettings.Enabled && 
            path.StartsWith("playlists/", StringComparison.OrdinalIgnoreCase) && 
            path.EndsWith("/items", StringComparison.OrdinalIgnoreCase))
        {
            // Extract playlist ID from path: playlists/{id}/items
            var parts = path.Split('/');
            if (parts.Length == 3)
            {
                var playlistId = parts[1];
                _logger.LogInformation("=== SPOTIFY INTERCEPTION === Checking playlist {PlaylistId}", playlistId);
                
                // Get playlist info from Jellyfin to check the name
                var playlistInfo = await _proxyService.GetJsonAsync($"Items/{playlistId}", null, Request.Headers);
                if (playlistInfo != null && playlistInfo.RootElement.TryGetProperty("Name", out var nameElement))
                {
                    var playlistName = nameElement.GetString() ?? "";
                    _logger.LogInformation("Playlist name: {PlaylistName}", playlistName);
                    
                    // Check if this matches any configured Spotify playlists
                    var matchingConfig = _spotifySettings.Playlists
                        .FirstOrDefault(p => p.Enabled && p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));
                    
                    if (matchingConfig != null)
                    {
                        _logger.LogInformation("=== INTERCEPTING SPOTIFY PLAYLIST === {PlaylistName}", playlistName);
                        return await GetSpotifyPlaylistTracksAsync(matchingConfig.SpotifyName);
                    }
                }
            }
        }
        
        // Handle non-JSON responses (robots.txt, etc.)
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || 
            path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            var fullPath = path;
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{path}{Request.QueryString.Value}";
            }
            
            var url = $"{_settings.Url?.TrimEnd('/')}/{fullPath}";
            
            try
            {
                var response = await _proxyService.HttpClient.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "text/plain";
                return Content(content, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to proxy non-JSON request for {Path}", path);
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
                
                result = await _proxyService.PostJsonAsync(fullPath, body, Request.Headers);
            }
            else
            {
                // Forward GET requests transparently with authentication headers and query string
                result = await _proxyService.GetJsonAsync(fullPath, null, Request.Headers);
            }
            
            if (result == null)
            {
                // Return 204 No Content for successful requests with no body
                // (e.g., /sessions/playing, /sessions/playing/progress)
                return NoContent();
            }

            return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxy request failed for {Path}", path);
            return _responseBuilder.CreateError(502, $"Proxy error: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

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
    /// Gets tracks for a Spotify playlist by matching missing tracks against external providers.
    /// </summary>
    private async Task<IActionResult> GetSpotifyPlaylistTracksAsync(string spotifyPlaylistName)
    {
        try
        {
            var matchingPlaylist = _spotifySettings.Playlists
                .FirstOrDefault(p => p.SpotifyName.Equals(spotifyPlaylistName, StringComparison.OrdinalIgnoreCase));

            if (matchingPlaylist == null)
            {
                _logger.LogWarning("Spotify playlist not found in config: {PlaylistName}", spotifyPlaylistName);
                return _responseBuilder.CreateItemsResponse(new List<Song>());
            }

            var cacheKey = $"spotify:matched:{matchingPlaylist.SpotifyName}";
            var cachedTracks = await _cache.GetAsync<List<Song>>(cacheKey);
            
            if (cachedTracks != null)
            {
                _logger.LogDebug("Returning {Count} cached matched tracks for {Playlist}", 
                    cachedTracks.Count, matchingPlaylist.Name);
                return _responseBuilder.CreateItemsResponse(cachedTracks);
            }

            var missingTracksKey = $"spotify:missing:{matchingPlaylist.SpotifyName}";
            var missingTracks = await _cache.GetAsync<List<allstarr.Models.Spotify.MissingTrack>>(missingTracksKey);
            
            if (missingTracks == null || missingTracks.Count == 0)
            {
                _logger.LogInformation("No missing tracks found for {Playlist}", matchingPlaylist.Name);
                return _responseBuilder.CreateItemsResponse(new List<Song>());
            }

            _logger.LogInformation("Matching {Count} tracks for {Playlist}", 
                missingTracks.Count, matchingPlaylist.Name);

            var matchTasks = missingTracks.Select(async track =>
            {
                try
                {
                    var query = $"{track.Title} {track.AllArtists} {track.Album}";
                    var results = await _metadataService.SearchSongsAsync(query, limit: 1);
                    return results.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to match track: {Title} - {Artist}", 
                        track.Title, track.PrimaryArtist);
                    return null;
                }
            });

            var matchedTracks = (await Task.WhenAll(matchTasks))
                .Where(t => t != null)
                .Cast<Song>()
                .ToList();

            await _cache.SetAsync(cacheKey, matchedTracks, TimeSpan.FromHours(1));

            _logger.LogInformation("Matched {Matched}/{Total} tracks for {Playlist}", 
                matchedTracks.Count, missingTracks.Count, matchingPlaylist.Name);

            return _responseBuilder.CreateItemsResponse(matchedTracks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Spotify playlist tracks {PlaylistName}", spotifyPlaylistName);
            return _responseBuilder.CreateError(500, "Failed to get Spotify playlist tracks");
        }
    }

    #endregion
}
// force rebuild Sun Jan 25 13:22:47 EST 2026
