using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Models.Scrobbling;
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
using allstarr.Services.Scrobbling;
using allstarr.Services.Admin;
using allstarr.Services.SquidWTF;
using allstarr.Filters;

namespace allstarr.Controllers;

/// <summary>
/// Jellyfin-compatible API controller. Merges local library with external providers
/// (Deezer, Qobuz, SquidWTF). Auth goes through Jellyfin.
/// </summary>
[ApiController]
[Route("")]
public partial class JellyfinController : ControllerBase
{
    private readonly JellyfinSettings _settings;
    private readonly SpotifyImportSettings _spotifySettings;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly ScrobblingSettings _scrobblingSettings;
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
    private readonly LyricsPlusService? _lyricsPlusService;
    private readonly LrclibService? _lrclibService;
    private readonly LyricsOrchestrator? _lyricsOrchestrator;
    private readonly ScrobblingOrchestrator? _scrobblingOrchestrator;
    private readonly ScrobblingHelper? _scrobblingHelper;
    private readonly OdesliService _odesliService;
    private readonly RedisCacheService _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JellyfinController> _logger;

    public JellyfinController(
        IOptions<JellyfinSettings> settings,
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<ScrobblingSettings> scrobblingSettings,
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
        LyricsPlusService? lyricsPlusService = null,
        LrclibService? lrclibService = null,
        LyricsOrchestrator? lyricsOrchestrator = null,
        ScrobblingOrchestrator? scrobblingOrchestrator = null,
        ScrobblingHelper? scrobblingHelper = null)
    {
        _settings = settings.Value;
        _spotifySettings = spotifySettings.Value;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _scrobblingSettings = scrobblingSettings.Value;
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
        _lyricsPlusService = lyricsPlusService;
        _lrclibService = lrclibService;
        _lyricsOrchestrator = lyricsOrchestrator;
        _scrobblingOrchestrator = scrobblingOrchestrator;
        _scrobblingHelper = scrobblingHelper;
        _odesliService = odesliService;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.Url))
        {
            throw new InvalidOperationException("JELLYFIN_URL environment variable is not set");
        }
    }

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
            return await GetExternalItem(provider!, type, externalId!, HttpContext.RequestAborted);
        }

        // Proxy to Jellyfin using the same route shape and query string the client sent.
        var endpoint = !string.IsNullOrWhiteSpace(userId)
            ? $"Users/{userId}/Items/{itemId}"
            : $"Items/{itemId}";

        if (Request.QueryString.HasValue)
        {
            endpoint = $"{endpoint}{Request.QueryString.Value}";
        }

        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

        return HandleProxyResponse(result, statusCode);
    }

    /// <summary>
    /// Gets an external item (song, album, or artist).
    /// </summary>
    private async Task<IActionResult> GetExternalItem(string provider, string? type, string externalId, CancellationToken cancellationToken = default)
    {
        switch (type)
        {
            case "song":
                var song = await _metadataService.GetSongAsync(provider, externalId, cancellationToken);
                if (song == null) return _responseBuilder.CreateError(404, "Song not found");
                return _responseBuilder.CreateSongResponse(song);

            case "album":
                var album = await _metadataService.GetAlbumAsync(provider, externalId, cancellationToken);
                if (album == null) return _responseBuilder.CreateError(404, "Album not found");
                return _responseBuilder.CreateAlbumResponse(album);

            case "artist":
                var artist = await _metadataService.GetArtistAsync(provider, externalId, cancellationToken);
                if (artist == null) return _responseBuilder.CreateError(404, "Artist not found");
                var albums = await _metadataService.GetArtistAlbumsAsync(provider, externalId, cancellationToken);

                // Fill in artist info for albums
                foreach (var a in albums)
                {
                    if (string.IsNullOrEmpty(a.Artist)) a.Artist = artist.Name;
                    if (string.IsNullOrEmpty(a.ArtistId)) a.ArtistId = artist.Id;
                }

                return _responseBuilder.CreateArtistResponse(artist, albums);

            default:
                // Try song first, then album
                var s = await _metadataService.GetSongAsync(provider, externalId, cancellationToken);
                if (s != null) return _responseBuilder.CreateSongResponse(s);

                var alb = await _metadataService.GetAlbumAsync(provider, externalId, cancellationToken);
                if (alb != null) return _responseBuilder.CreateAlbumResponse(alb);

                return _responseBuilder.CreateError(404, "Item not found");
        }
    }

    /// <summary>
    /// Gets child items for an external parent (album tracks or artist albums).
    /// </summary>
    private async Task<IActionResult> GetExternalChildItems(string provider, string type, string externalId, string? includeItemTypes, CancellationToken cancellationToken = default)
    {
        if (IsFavoritesOnlyRequest())
        {
            _logger.LogDebug(
                "Suppressing external child items for favorites-only request: provider={Provider}, type={Type}, externalId={ExternalId}",
                provider,
                type,
                externalId);
            return CreateEmptyItemsResponse(GetRequestedStartIndex());
        }

        var itemTypes = ParseItemTypes(includeItemTypes);
        var itemTypesUnspecified = itemTypes == null || itemTypes.Length == 0;

        _logger.LogDebug("GetExternalChildItems: provider={Provider}, type={Type}, externalId={ExternalId}, itemTypes={ItemTypes}",
            provider, type, externalId, string.Join(",", itemTypes ?? Array.Empty<string>()));

        // Albums are track containers in Jellyfin clients; when ParentId points to an album,
        // return tracks even if IncludeItemTypes is omitted.
        if (type == "album" && (itemTypesUnspecified || itemTypes!.Contains("Audio", StringComparer.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("Fetching album tracks for {Provider}/{ExternalId}", provider, externalId);
            var album = await _metadataService.GetAlbumAsync(provider, externalId, cancellationToken);
            if (album == null)
            {
                return _responseBuilder.CreateError(404, "Album not found");
            }

            var sortedAndPagedSongs = ApplySongSortAndPagingForCurrentRequest(album.Songs, out var totalRecordCount, out var startIndex);
            var items = sortedAndPagedSongs.Select(_responseBuilder.ConvertSongToJellyfinItem).ToList();

            return _responseBuilder.CreateJsonResponse(new
            {
                Items = items,
                TotalRecordCount = totalRecordCount,
                StartIndex = startIndex
            });
        }

        // Check if asking for audio (artist songs)
        if (itemTypes?.Contains("Audio", StringComparer.OrdinalIgnoreCase) == true)
        {
            if (type == "artist")
            {
                // For artist + Audio, fetch top tracks from the artist endpoint
                _logger.LogDebug("Fetching artist tracks for {Provider}/{ExternalId}", provider, externalId);
                var tracks = await _metadataService.GetArtistTracksAsync(provider, externalId, cancellationToken);

                if (tracks == null)
                {
                    _logger.LogWarning("No tracks found for artist {Provider}/{ExternalId}", provider, externalId);
                    return _responseBuilder.CreateItemsResponse(new List<Song>());
                }

                _logger.LogDebug("Found {Count} tracks for artist", tracks.Count);
                return _responseBuilder.CreateItemsResponse(tracks);
            }
        }

        // Check if asking for albums (artist albums)
        if (itemTypes?.Contains("MusicAlbum", StringComparer.OrdinalIgnoreCase) == true || itemTypesUnspecified)
        {
            if (type == "artist")
            {
                _logger.LogDebug("Fetching artist albums for {Provider}/{ExternalId}", provider, externalId);
                var albums = await _metadataService.GetArtistAlbumsAsync(provider, externalId, cancellationToken);
                var artist = await _metadataService.GetArtistAsync(provider, externalId, cancellationToken);

                _logger.LogDebug("Found {Count} albums for artist {ArtistName}", albums.Count, artist?.Name ?? "unknown");

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
        }

        // Fallback: return empty result
        _logger.LogWarning("Unhandled GetExternalChildItems request: provider={Provider}, type={Type}, externalId={ExternalId}, itemTypes={ItemTypes}",
            provider, type, externalId, string.Join(",", itemTypes ?? Array.Empty<string>()));
        return _responseBuilder.CreateItemsResponse(new List<Song>());
    }

    private int GetRequestedStartIndex()
    {
        return int.TryParse(Request.Query["StartIndex"], out var startIndex) && startIndex > 0
            ? startIndex
            : 0;
    }

    private List<Song> ApplySongSortAndPagingForCurrentRequest(IReadOnlyCollection<Song> songs, out int totalRecordCount, out int startIndex)
    {
        var sortBy = Request.Query["SortBy"].ToString();
        var sortOrder = Request.Query["SortOrder"].ToString();
        var descending = sortOrder.Equals("Descending", StringComparison.OrdinalIgnoreCase);
        var sortFields = ParseSortFields(sortBy);

        var sortedSongs = songs.ToList();
        sortedSongs.Sort((left, right) => CompareSongs(left, right, sortFields, descending));

        totalRecordCount = sortedSongs.Count;
        startIndex = 0;
        if (int.TryParse(Request.Query["StartIndex"], out var parsedStartIndex) && parsedStartIndex > 0)
        {
            startIndex = parsedStartIndex;
        }

        if (int.TryParse(Request.Query["Limit"], out var parsedLimit) && parsedLimit > 0)
        {
            return sortedSongs.Skip(startIndex).Take(parsedLimit).ToList();
        }

        return sortedSongs.Skip(startIndex).ToList();
    }

    private static int CompareSongs(Song left, Song right, IReadOnlyList<string> sortFields, bool descending)
    {
        var effectiveSortFields = sortFields.Count > 0
            ? sortFields
            : new[] { "ParentIndexNumber", "IndexNumber", "SortName" };

        foreach (var field in effectiveSortFields)
        {
            var comparison = CompareSongsByField(left, right, field);
            if (comparison == 0)
            {
                continue;
            }

            return descending ? -comparison : comparison;
        }

        return string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareSongsByField(Song left, Song right, string field)
    {
        return field.ToLowerInvariant() switch
        {
            "parentindexnumber" => Nullable.Compare(left.DiscNumber, right.DiscNumber),
            "indexnumber" => Nullable.Compare(left.Track, right.Track),
            "sortname" => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase),
            "name" => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase),
            "datecreated" => Nullable.Compare(left.Year, right.Year),
            "productionyear" => Nullable.Compare(left.Year, right.Year),
            _ => 0
        };
    }

    private static List<string> ParseSortFields(string sortBy)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return new List<string>();
        }

        return sortBy
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .ToList();
    }
    private async Task<IActionResult> GetCuratorPlaylists(string provider, string externalId, string? includeItemTypes, CancellationToken cancellationToken = default)
        {
            var itemTypes = ParseItemTypes(includeItemTypes);

            _logger.LogDebug("GetCuratorPlaylists: provider={Provider}, curatorId={CuratorId}, itemTypes={ItemTypes}",
                provider, externalId, string.Join(",", itemTypes ?? Array.Empty<string>()));

            // Extract curator name from externalId (format: "curator-{name}")
            var curatorName = externalId.Replace("curator-", "", StringComparison.OrdinalIgnoreCase);

            // Search for playlists by this curator
            // Since we don't have a direct "get playlists by curator" method, we'll search for the curator name
            // and filter the results
            var playlists = await _metadataService.SearchPlaylistsAsync(curatorName, 50, cancellationToken);

            // Filter to only playlists from this curator (case-insensitive match)
            var curatorPlaylists = playlists
                .Where(p => !string.IsNullOrEmpty(p.CuratorName) &&
                           p.CuratorName.Equals(curatorName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.LogDebug("Found {Count} playlists for curator '{CuratorName}'", curatorPlaylists.Count, curatorName);

            // Convert playlists to album items
            var albumItems = curatorPlaylists
                .Select(p => _responseBuilder.ConvertPlaylistToAlbumItem(p))
                .ToList();

            var response = new Dictionary<string, object>
            {
                ["Items"] = albumItems,
                ["TotalRecordCount"] = albumItems.Count,
                ["StartIndex"] = 0
            };

            return new JsonResult(response);
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
        _logger.LogDebug("GetArtists called: searchTerm={SearchTerm}, limit={Limit}", searchTerm, limit);

        // If there's a search term, integrate external results
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var cleanQuery = searchTerm.Trim().Trim('"');
            _logger.LogDebug("Searching artists for: {Query}", cleanQuery);

            // Run local and external searches in parallel
            var jellyfinTask = GetLocalArtistsResultForCurrentRequest(cleanQuery);

            // Use parallel metadata service if available (races providers), otherwise use primary
            Task<List<Artist>> externalTask;
            if (_parallelMetadataService != null)
            {
                externalTask = _parallelMetadataService.SearchAllAsync(cleanQuery, 0, 0, limit, HttpContext.RequestAborted)
                    .ContinueWith(t => t.Result.Artists, HttpContext.RequestAborted);
            }
            else
            {
                externalTask = _metadataService.SearchArtistsAsync(cleanQuery, limit, HttpContext.RequestAborted);
            }

            await Task.WhenAll(jellyfinTask, externalTask);

            var (jellyfinResult, _) = await jellyfinTask;
            var externalArtists = await externalTask;

            _logger.LogDebug("Artist search results: Jellyfin={JellyfinCount}, External={ExternalCount}",
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

            _logger.LogDebug("Returning {Count} total artists (local + external, no deduplication)", mergedArtists.Count);

            // Convert to Jellyfin format
            var artistItems = mergedArtists.Select(a => _responseBuilder.ConvertArtistToJellyfinItem(a)).ToList();

            return _responseBuilder.CreateJsonResponse(new
            {
                Items = artistItems,
                TotalRecordCount = artistItems.Count,
                StartIndex = startIndex
            });
        }

        // No search term - proxy the literal request route and query string to Jellyfin
        var endpoint = Request.Path.Value?.TrimStart('/') ?? "Artists";
        if (Request.QueryString.HasValue)
        {
            endpoint = $"{endpoint}{Request.QueryString.Value}";
        }

        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

        return HandleProxyResponse(result, statusCode);
    }

    private async Task<(JsonDocument? Body, int StatusCode)> GetLocalArtistsResultForCurrentRequest(string cleanQuery)
    {
        var endpoint = Request.Path.Value?.TrimStart('/') ?? "Artists";

        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in Request.Query)
        {
            queryParams[kvp.Key] = kvp.Value.ToString();
        }

        // Preserve literal request semantics, only normalize recovered SearchTerm.
        queryParams["SearchTerm"] = cleanQuery;

        _logger.LogInformation(
            "SEARCH TRACE: local artists proxy request endpoint='{Endpoint}' query='{SafeQuery}'",
            endpoint,
            ToSafeQueryStringForLogs(queryParams));

        return await _proxyService.GetJsonAsync(endpoint, queryParams, Request.Headers);
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
        var externalArtists = await _metadataService.SearchArtistsAsync(artistName, 1, HttpContext.RequestAborted);
        var externalAlbums = new List<Album>();

        if (externalArtists.Count > 0)
        {
            var extArtist = externalArtists[0];
            if (extArtist.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            {
                externalAlbums = await _metadataService.GetArtistAlbumsAsync("deezer", extArtist.ExternalId!, HttpContext.RequestAborted);

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
        [FromQuery] int? maxHeight = null,
        [FromQuery(Name = "tag")] string? tag = null)
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
                maxHeight,
                tag);

            if (imageBytes == null || contentType == null)
            {
                // Try to get the item details to find fallback image (album/parent)
                var (itemResult, itemStatus) = await _proxyService.GetJsonAsync($"Items/{itemId}", null, Request.Headers);

                if (itemResult != null && itemStatus == 200)
                {
                    var item = itemResult.RootElement;
                    string? fallbackItemId = null;

                    // Check for album image fallback (for songs)
                    if (item.TryGetProperty("AlbumId", out var albumIdProp))
                    {
                        fallbackItemId = albumIdProp.GetString();
                    }
                    // Check for parent primary image fallback
                    else if (item.TryGetProperty("ParentPrimaryImageItemId", out var parentIdProp))
                    {
                        fallbackItemId = parentIdProp.GetString();
                    }

                    // Try to fetch the fallback image
                    if (!string.IsNullOrEmpty(fallbackItemId))
                    {
                        _logger.LogDebug("Item {ItemId} has no {ImageType} image, trying fallback from {FallbackId}",
                            itemId, imageType, fallbackItemId);

                        var (fallbackBytes, fallbackContentType) = await _proxyService.GetImageAsync(
                            fallbackItemId,
                            imageType,
                            maxWidth,
                            maxHeight);

                        if (fallbackBytes != null && fallbackContentType != null)
                        {
                            return File(fallbackBytes, fallbackContentType);
                        }
                    }
                }

                // Return placeholder if no fallback found
                return await GetPlaceholderImageAsync();
            }

            return File(imageBytes, contentType);
        }

        // Check Redis cache for previously fetched external image
        var imageCacheKey = CacheKeyBuilder.BuildExternalImageKey(provider!, type!, externalId!);
        var cachedImageBytes = await _cache.GetAsync<byte[]>(imageCacheKey);
        if (cachedImageBytes != null)
        {
            _logger.LogDebug("Cache hit for external {Type} image: {Provider}/{ExternalId}", type, provider, externalId);
            return File(cachedImageBytes, "image/jpeg");
        }

        // Get external cover art URL
        string? coverUrl = type switch
        {
            "artist" => (await _metadataService.GetArtistAsync(provider!, externalId!))?.ImageUrl,
            "album" => (await _metadataService.GetAlbumAsync(provider!, externalId!))?.CoverArtUrl,
            "song" => (await _metadataService.GetSongAsync(provider!, externalId!))?.CoverArtUrl,
            _ => null
        };

        _logger.LogDebug("External {Type} {Provider}/{ExternalId} has cover URL: {HasCoverUrl}",
            type, provider, externalId, !string.IsNullOrEmpty(coverUrl));

        if (string.IsNullOrEmpty(coverUrl))
        {
            _logger.LogDebug("No cover URL for external {Type}, returning placeholder", type);
            // Return placeholder "no image available" image
            return await GetPlaceholderImageAsync();
        }

        if (!OutboundRequestGuard.TryCreateSafeHttpUri(coverUrl, out var validatedCoverUri, out var validationReason) ||
            validatedCoverUri == null)
        {
            _logger.LogWarning(
                "Blocked external image URL for {Type} {Provider}/{ExternalId}: {Reason}",
                type,
                provider,
                externalId,
                validationReason);
            return await GetPlaceholderImageAsync();
        }

        var safeCoverUri = validatedCoverUri!;

        // Fetch and return the image using the proxy service's HttpClient
        try
        {
            _logger.LogDebug("Fetching external image from host {Host}", safeCoverUri.Host);

            var imageBytes = await RetryHelper.RetryWithBackoffAsync(async () =>
            {
                var response = await _proxyService.HttpClient.GetAsync(safeCoverUri);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    throw new HttpRequestException($"Transient error: {response.StatusCode}", null, response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch external image from host {Host}: {StatusCode}",
                        safeCoverUri.Host, response.StatusCode);
                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync();
            }, _logger, maxRetries: 3, initialDelayMs: 500);

            if (imageBytes == null)
            {
                return await GetPlaceholderImageAsync();
            }

            // Cache the fetched image bytes in Redis for future requests
            await _cache.SetAsync(imageCacheKey, imageBytes, CacheExtensions.ProxyImagesTTL);

            _logger.LogDebug("Successfully fetched and cached external image from host {Host}, size: {Size} bytes",
                safeCoverUri.Host, imageBytes.Length);
            return File(imageBytes, "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch cover art from host {Host}", safeCoverUri.Host);
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

        _logger.LogDebug("MarkFavorite called: userId={UserId}, itemId={ItemId}, route={Route}",
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
            // Check if it's an album by parsing the full ID with type
            var (_, _, type, _) = _localLibraryService.ParseExternalId(itemId);

            if (type == "album")
            {
                _logger.LogInformation("Favoriting external album {ItemId}, downloading all tracks to kept folder", itemId);

                // Download entire album to kept folder in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CopyExternalAlbumToKeptAsync(itemId, provider!, externalId!);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to copy external album {ItemId} to kept folder", itemId);
                    }
                });
            }
            else
            {
                _logger.LogInformation("Favoriting external track {ItemId}, copying to kept folder", itemId);

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
            }

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

        _logger.LogDebug("Proxying favorite request to Jellyfin: {Endpoint}", endpoint);

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

        _logger.LogDebug("UnmarkFavorite called: userId={UserId}, itemId={ItemId}, route={Route}",
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

        _logger.LogDebug("Proxying unfavorite request to Jellyfin: {Endpoint}", endpoint);

        var (result, statusCode) = await _proxyService.DeleteAsync(endpoint, Request.Headers);

        return HandleProxyResponse(result, statusCode);
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
        var isRawSquidTrackId = !isExternal && long.TryParse(itemId, out _);
        var squidTrackId = provider?.Equals("squidwtf", StringComparison.OrdinalIgnoreCase) == true
            ? externalId
            : (isRawSquidTrackId ? itemId : null);

        if (isExternal || !string.IsNullOrWhiteSpace(squidTrackId))
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
                if (!string.IsNullOrWhiteSpace(squidTrackId) &&
                    _metadataService is SquidWTFMetadataService squidWtfMetadataService)
                {
                    var recommendations = await squidWtfMetadataService
                        .GetTrackRecommendationsAsync(squidTrackId, limit, HttpContext.RequestAborted);

                    var recommendedItems = recommendations
                        .Select(s => _responseBuilder.ConvertSongToJellyfinItem(s))
                        .ToList();

                    _logger.LogInformation(
                        "SQUIDWTF similar lookup: itemId={ItemId}, trackId={TrackId}, recommendations={Count}",
                        itemId,
                        squidTrackId,
                        recommendedItems.Count);

                    return _responseBuilder.CreateJsonResponse(new
                    {
                        Items = recommendedItems,
                        TotalRecordCount = recommendedItems.Count
                    });
                }

                if (!isExternal)
                {
                    _logger.LogDebug("Similar lookup skipped for non-external item {ItemId}", itemId);
                    return _responseBuilder.CreateJsonResponse(new
                    {
                        Items = Array.Empty<object>(),
                        TotalRecordCount = 0
                    });
                }

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
                    .Where(s => !string.Equals(s.ExternalId, externalId, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(s.Id, itemId, StringComparison.OrdinalIgnoreCase))
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
                _logger.LogError(ex, "Failed to get similar items for external song {ItemId}", itemId);
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

        // Preserve full client query string to keep Jellyfin behavior consistent for all supported params
        if (Request.QueryString.HasValue)
        {
            endpoint = $"{endpoint}{Request.QueryString.Value}";
        }

        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

        return HandleProxyResponse(result, statusCode);
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
                _logger.LogError(ex, "Failed to create instant mix for external song {ItemId}", itemId);
                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = Array.Empty<object>(),
                    TotalRecordCount = 0
                });
            }
        }

        // For local items, proxy using the same route shape and full query string from the client
        var endpoint = Request.Path.Value?.Contains("/Items/", StringComparison.OrdinalIgnoreCase) == true
            ? $"Items/{itemId}/InstantMix"
            : $"Songs/{itemId}/InstantMix";

        if (Request.QueryString.HasValue)
        {
            endpoint = $"{endpoint}{Request.QueryString.Value}";
        }

        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

        return HandleProxyResponse(result, statusCode);
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
            Version = version ?? AppVersion.Version,
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
        // Block admin API routes - these should be handled by admin controllers, not proxied to Jellyfin
        if (path.StartsWith("api/admin", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Admin route {Path} reached ProxyRequest - this should be handled by admin controllers", path);
            return NotFound(new { error = "Admin endpoint not found" });
        }

        // Log session-related requests prominently to debug missing capabilities call
        if (path.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("capabilit", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("🔍 SESSION/CAPABILITY REQUEST: {Method} /{Path}{Query}", Request.Method, path,
                MaskSensitiveQueryString(Request.QueryString.Value));
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

        var playlistItemsRequestId = GetExactPlaylistItemsRequestId(path);
        if (!string.IsNullOrEmpty(playlistItemsRequestId))
        {
            if (_spotifySettings.Enabled)
            {
                _logger.LogDebug("=== PLAYLIST REQUEST ===");
                _logger.LogInformation("Playlist ID: {PlaylistId}", playlistItemsRequestId);
                _logger.LogInformation("Spotify Enabled: {Enabled}", _spotifySettings.Enabled);
                _logger.LogInformation("Configured Playlists: {Playlists}", string.Join(", ", _spotifySettings.Playlists.Select(p => $"{p.Name}:{p.Id}")));
                _logger.LogInformation("Is configured: {IsConfigured}", _spotifySettings.IsSpotifyPlaylist(playlistItemsRequestId));

                // Check if this playlist ID is configured for Spotify injection
                if (_spotifySettings.IsSpotifyPlaylist(playlistItemsRequestId))
                {
                    _logger.LogInformation("========================================");
                    _logger.LogInformation("=== INTERCEPTING SPOTIFY PLAYLIST ===");
                    _logger.LogInformation("Playlist ID: {PlaylistId}", playlistItemsRequestId);
                    _logger.LogInformation("========================================");
                    return await GetPlaylistTracks(playlistItemsRequestId);
                }
            }

            var playlistItemsPath = path;
            if (Request.QueryString.HasValue)
            {
                playlistItemsPath = $"{playlistItemsPath}{Request.QueryString.Value}";
            }

            _logger.LogDebug("Using transparent Jellyfin passthrough for non-injected playlist {PlaylistId}",
                playlistItemsRequestId);
            return await ProxyJsonPassthroughAsync(playlistItemsPath);
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
                AuthHeaderHelper.ForwardAuthHeaders(Request.Headers, request);

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
                _logger.LogError(ex, "Failed to proxy binary request for {Path}", path);
                return NotFound();
            }
        }

        // Check if this is a search request that should be handled by specific endpoints
        var searchTerm = Request.Query["SearchTerm"].ToString();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            _logger.LogDebug("ProxyRequest intercepting search request: Path={Path}, SearchTerm={SearchTerm}", path, searchTerm);

            // Item search: /users/{userId}/items or /items
            if (path.EndsWith("/items", StringComparison.OrdinalIgnoreCase) || path.Equals("items", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Redirecting to SearchItems");
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
            var safePathForLogs = path;
            if (Request.QueryString.HasValue)
            {
                fullPath = $"{path}{Request.QueryString.Value}";
                safePathForLogs = $"{path}{MaskSensitiveQueryString(Request.QueryString.Value)}";
            }

            JsonDocument? result;
            int statusCode;

            if (HttpContext.Request.Method == HttpMethod.Post.Method)
            {
                // Enable buffering BEFORE any reads
                Request.EnableBuffering();

                // Log request details for debugging
                _logger.LogDebug("POST request to {Path}: Method={Method}, ContentType={ContentType}, ContentLength={ContentLength}",
                    safePathForLogs, Request.Method, Request.ContentType, Request.ContentLength);

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
                        safePathForLogs, Request.ContentLength, Request.ContentType);
                    _logger.LogWarning("Empty POST body metadata: HeaderCount={HeaderCount}", Request.Headers.Count);
                }
                else
                {
                    _logger.LogDebug("POST body received from client for {Path}: {BodyLength} bytes, ContentType={ContentType}",
                        safePathForLogs, body.Length, Request.ContentType);
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
            if (ShouldProcessSpotifyPlaylistCounts(result, Request.Query["IncludeItemTypes"].ToString()))
            {
                _logger.LogDebug("Response has Items property, checking for Spotify playlists to update counts");
                result = await UpdateSpotifyPlaylistCounts(result);
            }

            // Return the raw JSON element directly to avoid deserialization issues with simple types
            return new JsonResult(result.RootElement.Clone());
        }
        catch (HttpRequestException httpEx)
        {
            // HTTP-specific errors - preserve the status code if available
            var statusCode = httpEx.StatusCode.HasValue ? (int)httpEx.StatusCode.Value : 502;

            _logger.LogError(httpEx, "HTTP error proxying request to Jellyfin for {Path}: {StatusCode}", path, statusCode);

            // Return appropriate status code based on the error
            if (statusCode == 404)
            {
                return NotFound();
            }
            else if (statusCode >= 400 && statusCode < 500)
            {
                return StatusCode(statusCode, new { error = $"Jellyfin returned {statusCode}" });
            }
            else
            {
                return StatusCode(502, new { error = "Failed to connect to Jellyfin server" });
            }
        }
        catch (TaskCanceledException)
        {
            // Request was cancelled (timeout or client disconnect)
            _logger.LogWarning("Proxy request cancelled or timed out for {Path}", path);
            return StatusCode(504, new { error = "Request to Jellyfin timed out" });
        }
        catch (Exception ex)
        {
            // Generic error - return 502 Bad Gateway
            _logger.LogError(ex, "Proxy request failed for {Path}", path);
            return _responseBuilder.CreateError(502, "Proxy error");
        }
    }

    #endregion

    /// <summary>
    /// Checks if an item dictionary represents a local Jellyfin item (not external).
    /// </summary>
    private bool IsLocalItem(Dictionary<string, object?> item)
    {
        if (!item.TryGetValue("Id", out var idObj)) return false;

        var id = idObj is JsonElement idEl ? idEl.GetString() : idObj?.ToString();
        if (string.IsNullOrEmpty(id)) return false;

        // External items have IDs starting with "ext-"
        return !id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts a JsonElement to a Dictionary while properly preserving nested objects and arrays.
    /// This prevents metadata from being stripped when deserializing Jellyfin responses.
    /// </summary>
    private Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();

        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = ConvertJsonElement(property.Value);
        }

        return dict;
    }

    /// <summary>
    /// Recursively converts JsonElement values to proper C# types (Dictionary, List, primitives).
    /// </summary>
    private object? ConvertJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = ConvertJsonElement(property.Value);
                }
                return dict;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ConvertJsonElement(item));
                }
                return list;

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
                if (element.TryGetInt32(out var intValue))
                    return intValue;
                if (element.TryGetInt64(out var longValue))
                    return longValue;
                if (element.TryGetDouble(out var doubleValue))
                    return doubleValue;
                return element.GetDecimal();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
                return null;

            default:
                return null;
        }
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
                var cacheKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlist.Name);
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
            _logger.LogError(ex, "Error finding Spotify ID for external track");
            return null;
        }
    }
}

// force rebuild Sun Jan 25 13:22:47 EST 2026
