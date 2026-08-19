using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Models.Domain;
using allstarr.Models.Scrobbling;
using allstarr.Models.Settings;
using allstarr.Models.Subsonic;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using allstarr.Services.Spotify;
using allstarr.Services.Scrobbling;
using allstarr.Services.Admin;
using allstarr.Filters;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols;
using allstarr.Core.Favorites;
using allstarr.Core.Playback;
using allstarr.Core.Intelligence;
using SkiaSharp;

namespace allstarr.Controllers;

/// <summary>
/// Jellyfin-compatible API controller. Merges local library with external providers
/// (Deezer, Qobuz, Apple Music, and extensions). Auth goes through Jellyfin.
/// </summary>
[ApiController]
[Route("")]
[ServiceFilter(typeof(JellyfinAuthFilter), Order = int.MinValue)]
[ServiceFilter(typeof(ProtocolExecutionContextFilter), Order = int.MinValue + 1)]
public partial class JellyfinController : ControllerBase
{
    private const int MaximumArtworkBytes = 10 * 1024 * 1024;
    private static readonly byte[] PlaceholderImageBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly JellyfinSettings _settings;
    private readonly SpotifyImportSettings _spotifySettings;
    private readonly ScrobblingSettings _scrobblingSettings;
    private readonly IMusicMetadataService _metadataService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly JellyfinResponseBuilder _responseBuilder;
    private readonly IJellyfinSearchProtocolAdapter _searchProtocolAdapter;
    private readonly IJellyfinItemProtocolAdapter _itemProtocolAdapter;
    private readonly IJellyfinImageProtocolAdapter _imageProtocolAdapter;
    private readonly IJellyfinLyricsProtocolAdapter _lyricsProtocolAdapter;
    private readonly IJellyfinInteractionProtocolAdapter _interactionProtocolAdapter;
    private readonly JellyfinVirtualPlaylistProtocolAdapter _virtualPlaylistProtocolAdapter;
    private readonly ProtocolStreamingResponseAdapter _streamingResponseAdapter;
    private readonly JellyfinModelMapper _modelMapper;
    private readonly JellyfinProxyService _proxyService;
    private readonly JellyfinSessionManager _sessionManager;
    private readonly IProtocolLyricsResolver _protocolLyricsResolver;
    private readonly ScrobblingHelper? _scrobblingHelper;
    private readonly IApplicationCache _cache;
    private readonly IMediaAssetResolver _mediaAssets;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JellyfinController> _logger;
    private readonly IFavoriteActionPipeline? _favoriteActions;
    private readonly IPlaybackSignalPipeline? _playbackSignals;
    private readonly IProtocolProviderGateway? _providerGateway;
    private readonly IAudioMuseRecommendationClient? _audioMuse;
    private readonly IProtocolLibraryScopeResolver? _libraryScopes;
    private readonly IIntelligencePolicyService? _intelligencePolicies;
    private readonly ManagedTrackCacheService? _managedTrackCache;

    public JellyfinController(
        IOptions<JellyfinSettings> settings,
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<ScrobblingSettings> scrobblingSettings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        IDownloadService downloadService,
        JellyfinResponseBuilder responseBuilder,
        IJellyfinSearchProtocolAdapter searchProtocolAdapter,
        IJellyfinItemProtocolAdapter itemProtocolAdapter,
        IJellyfinImageProtocolAdapter imageProtocolAdapter,
        IJellyfinLyricsProtocolAdapter lyricsProtocolAdapter,
        IJellyfinInteractionProtocolAdapter interactionProtocolAdapter,
        JellyfinVirtualPlaylistProtocolAdapter virtualPlaylistProtocolAdapter,
        ProtocolStreamingResponseAdapter streamingResponseAdapter,
        JellyfinModelMapper modelMapper,
        JellyfinProxyService proxyService,
        JellyfinSessionManager sessionManager,
        IApplicationCache cache,
        IMediaAssetResolver mediaAssets,
        IProtocolLyricsResolver protocolLyricsResolver,
        IConfiguration configuration,
        ILogger<JellyfinController> logger,
        ScrobblingHelper? scrobblingHelper = null,
        IFavoriteActionPipeline? favoriteActions = null,
        IPlaybackSignalPipeline? playbackSignals = null,
        IProtocolProviderGateway? providerGateway = null,
        IAudioMuseRecommendationClient? audioMuse = null,
        IProtocolLibraryScopeResolver? libraryScopes = null,
        IIntelligencePolicyService? intelligencePolicies = null,
        ManagedTrackCacheService? managedTrackCache = null)
    {
        _settings = settings.Value;
        _spotifySettings = spotifySettings.Value;
        _scrobblingSettings = scrobblingSettings.Value;
        _metadataService = metadataService;
        _localLibraryService = localLibraryService;
        _downloadService = downloadService;
        _responseBuilder = responseBuilder;
        _searchProtocolAdapter = searchProtocolAdapter;
        _itemProtocolAdapter = itemProtocolAdapter;
        _imageProtocolAdapter = imageProtocolAdapter;
        _lyricsProtocolAdapter = lyricsProtocolAdapter;
        _interactionProtocolAdapter = interactionProtocolAdapter;
        _virtualPlaylistProtocolAdapter = virtualPlaylistProtocolAdapter;
        _streamingResponseAdapter = streamingResponseAdapter;
        _modelMapper = modelMapper;
        _proxyService = proxyService;
        _sessionManager = sessionManager;
        _protocolLyricsResolver = protocolLyricsResolver;
        _scrobblingHelper = scrobblingHelper;
        _cache = cache;
        _mediaAssets = mediaAssets;
        _configuration = configuration;
        _logger = logger;
        _favoriteActions = favoriteActions;
        _playbackSignals = playbackSignals;
        _providerGateway = providerGateway;
        _audioMuse = audioMuse;
        _libraryScopes = libraryScopes;
        _intelligencePolicies = intelligencePolicies;
        _managedTrackCache = managedTrackCache;

        if (string.IsNullOrWhiteSpace(_settings.Url))
        {
            throw new InvalidOperationException("JELLYFIN_URL environment variable is not set");
        }
    }

    private Task<Song?> GetProviderSongAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default) => _providerGateway != null
        ? _providerGateway.GetSongAsync(HttpContext.RequireProtocolExecutionContext(), provider, externalId)
        : _metadataService.GetSongAsync(provider, externalId, cancellationToken);

    private Task<Album?> GetProviderAlbumAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default) => _providerGateway != null
        ? _providerGateway.GetAlbumAsync(HttpContext.RequireProtocolExecutionContext(), provider, externalId)
        : _metadataService.GetAlbumAsync(provider, externalId, cancellationToken);

    private Task<Artist?> GetProviderArtistAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default) => _providerGateway != null
        ? _providerGateway.GetArtistAsync(HttpContext.RequireProtocolExecutionContext(), provider, externalId)
        : _metadataService.GetArtistAsync(provider, externalId, cancellationToken);

    private Task<List<Album>> GetProviderArtistAlbumsAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default) => _providerGateway != null
        ? _providerGateway.GetArtistAlbumsAsync(HttpContext.RequireProtocolExecutionContext(), provider, externalId)
        : _metadataService.GetArtistAlbumsAsync(provider, externalId, cancellationToken);

    private Task<List<Song>> GetProviderArtistTracksAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default) => _providerGateway != null
        ? _providerGateway.GetArtistTracksAsync(HttpContext.RequireProtocolExecutionContext(), provider, externalId)
        : _metadataService.GetArtistTracksAsync(provider, externalId, cancellationToken);

    private async Task<IReadOnlyList<Song>> SearchProviderSongsAsync(
        string provider,
        string query,
        int limit,
        CancellationToken cancellationToken = default) => _providerGateway != null
        ? (await _providerGateway.SearchAsync(
            HttpContext.RequireProtocolExecutionContext(), query, limit, 0, 0, provider)).Songs
        : (await _metadataService.SearchSongsAsync(query, limit, cancellationToken))
            .Where(song => string.Equals(
                song.ExternalProvider, provider, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToArray();

    private Task<Song?> GetProviderSongForImageAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        var protocol = HttpContext.GetProtocolExecutionContext();
        return _providerGateway != null && protocol != null
            ? _providerGateway.GetSongAsync(protocol, provider, externalId)
            : _metadataService.GetSongAsync(provider, externalId, cancellationToken);
    }

    private Task<Album?> GetProviderAlbumForImageAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        var protocol = HttpContext.GetProtocolExecutionContext();
        return _providerGateway != null && protocol != null
            ? _providerGateway.GetAlbumAsync(protocol, provider, externalId)
            : _metadataService.GetAlbumAsync(provider, externalId, cancellationToken);
    }

    private Task<Artist?> GetProviderArtistForImageAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        var protocol = HttpContext.GetProtocolExecutionContext();
        return _providerGateway != null && protocol != null
            ? _providerGateway.GetArtistAsync(protocol, provider, externalId)
            : _metadataService.GetArtistAsync(provider, externalId, cancellationToken);
    }

    private Task<ExternalPlaylist?> GetProviderPlaylistForImageAsync(
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        var protocol = HttpContext.GetProtocolExecutionContext();
        return _providerGateway != null && protocol != null
            ? _providerGateway.GetPlaylistAsync(protocol, provider, externalId)
            : _metadataService.GetPlaylistAsync(provider, externalId, cancellationToken);
    }

    #region Items

    /// <summary>
    /// Gets a single item by ID.
    /// </summary>
    [HttpGet("Items/{itemId}", Order = 10)]
    [HttpGet("Users/{userId}/Items/{itemId}", Order = 10)]
    public async Task<IActionResult> GetItem(string itemId, string? userId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return _responseBuilder.CreateError(400, "Missing item ID");
        }

        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(itemId))
        {
            return await _virtualPlaylistProtocolAdapter.ReadItemAsync(
                       HttpContext.RequireProtocolExecutionContext(), itemId, HttpContext.RequestAborted)
                   ?? _responseBuilder.CreateError(404, "Playlist not found");
        }

        var linkedPlaylist = _spotifySettings.Enabled
            ? _spotifySettings.GetPlaylistByJellyfinId(itemId)
            : null;
        if (linkedPlaylist != null)
        {
            var projection = await _virtualPlaylistProtocolAdapter.ReadItemBySourceAsync(
                HttpContext.RequireProtocolExecutionContext(),
                "spotify",
                linkedPlaylist.Id,
                itemId,
                HttpContext.RequestAborted);
            if (projection != null) return projection;
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
                var song = await GetProviderSongAsync(provider, externalId, cancellationToken);
                if (song == null) return _itemProtocolAdapter.ShapeNotFound("Song");
                return _itemProtocolAdapter.ShapeSong(song);

            case "album":
                var album = await GetProviderAlbumAsync(provider, externalId, cancellationToken);
                if (album == null) return _itemProtocolAdapter.ShapeNotFound("Album");
                return _itemProtocolAdapter.ShapeAlbum(album);

            case "artist":
                var artist = await GetProviderArtistAsync(provider, externalId, cancellationToken);
                if (artist == null) return _itemProtocolAdapter.ShapeNotFound("Artist");
                var albums = await GetProviderArtistAlbumsAsync(provider, externalId, cancellationToken);

                // Fill in artist info for albums
                foreach (var a in albums)
                {
                    if (string.IsNullOrEmpty(a.Artist)) a.Artist = artist.Name;
                    if (string.IsNullOrEmpty(a.ArtistId)) a.ArtistId = artist.Id;
                }

                return _itemProtocolAdapter.ShapeArtist(artist, albums);

            default:
                // Try song first, then album
                var s = await GetProviderSongAsync(provider, externalId, cancellationToken);
                if (s != null) return _itemProtocolAdapter.ShapeSong(s);

                var alb = await GetProviderAlbumAsync(provider, externalId, cancellationToken);
                if (alb != null) return _itemProtocolAdapter.ShapeAlbum(alb);

                return _itemProtocolAdapter.ShapeNotFound("Item");
        }
    }

    /// <summary>
    /// Gets child items for an external parent (album tracks or artist albums).
    /// </summary>
    private enum ExternalArtistRelation
    {
        All,
        AlbumArtist,
        ContributingArtist
    }

    private async Task<IActionResult> GetExternalChildItems(
        string provider,
        string type,
        string externalId,
        string? includeItemTypes,
        CancellationToken cancellationToken = default,
        string? selectedArtistId = null,
        ExternalArtistRelation artistRelation = ExternalArtistRelation.All)
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
            var album = await GetProviderAlbumAsync(provider, externalId, cancellationToken);
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

        if (type == "artist")
        {
            var includeTracks = itemTypes?.Contains("Audio", StringComparer.OrdinalIgnoreCase) == true;
            var includeAlbums = itemTypesUnspecified ||
                                itemTypes!.Contains("MusicAlbum", StringComparer.OrdinalIgnoreCase);
            if (includeTracks || includeAlbums)
            {
                var tracksTask = includeTracks || artistRelation == ExternalArtistRelation.ContributingArtist
                    ? GetProviderArtistTracksAsync(provider, externalId, cancellationToken)
                    : Task.FromResult(new List<Song>());
                var albumsTask = includeAlbums || includeTracks
                    ? GetProviderArtistAlbumsAsync(provider, externalId, cancellationToken)
                    : Task.FromResult(new List<Album>());
                var artistTask = includeAlbums
                    ? GetProviderArtistAsync(provider, externalId, cancellationToken)
                    : Task.FromResult<Artist?>(null);
                await Task.WhenAll(tracksTask, albumsTask, artistTask);

                var tracks = await tracksTask;
                var albums = await albumsTask;
                var artist = await artistTask;

                if (artistRelation == ExternalArtistRelation.ContributingArtist)
                {
                    var knownAlbumIds = albums.Select(album => album.Id)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var requestedCount = int.TryParse(Request.Query["Limit"], out var requestedLimit)
                        ? Math.Clamp(requestedLimit, 1, 200)
                        : 50;
                    var lookupCount = (int)Math.Min(
                        200L,
                        (long)GetRequestedStartIndex() + requestedCount);
                    var appearanceLookups = tracks
                        .Select(track => track.AlbumId)
                        .Where(id => !string.IsNullOrWhiteSpace(id) && !knownAlbumIds.Contains(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(id => _localLibraryService.ParseExternalId(id!))
                        .Where(parsed => parsed.isExternal &&
                                         parsed.type == "album" &&
                                         string.Equals(parsed.provider, provider, StringComparison.OrdinalIgnoreCase))
                        .Take(lookupCount)
                        .Select(parsed => GetProviderAlbumAsync(
                            provider,
                            parsed.externalId!,
                            cancellationToken));
                    albums.AddRange((await Task.WhenAll(appearanceLookups)).OfType<Album>());
                    albums = albums.DistinctBy(album => album.Id, StringComparer.OrdinalIgnoreCase).ToList();
                }

                if (includeTracks && tracks.Count == 0)
                    tracks = albums.SelectMany(album => album.Songs)
                        .DistinctBy(song => song.Id, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                if (artistRelation != ExternalArtistRelation.All)
                {
                    bool HasArtistIdentity(Album album) =>
                        !string.IsNullOrWhiteSpace(album.ArtistId) ||
                        artist != null && !string.IsNullOrWhiteSpace(album.Artist);
                    bool IsPrimaryAlbum(Album album) => !string.IsNullOrWhiteSpace(album.ArtistId)
                        ? string.Equals(album.ArtistId, selectedArtistId, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(album.Artist, artist?.Name, StringComparison.OrdinalIgnoreCase);
                    var wantsPrimaryAlbums = artistRelation == ExternalArtistRelation.AlbumArtist;
                    albums = albums.Where(album =>
                            HasArtistIdentity(album) && IsPrimaryAlbum(album) == wantsPrimaryAlbums)
                        .ToList();

                    if (includeTracks)
                    {
                        var albumIds = albums.Select(album => album.Id)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        tracks = tracks.Where(track =>
                                !string.IsNullOrWhiteSpace(track.AlbumId) && albumIds.Contains(track.AlbumId))
                            .ToList();
                    }
                }

                if (artist != null && artistRelation != ExternalArtistRelation.ContributingArtist)
                {
                    foreach (var album in albums)
                    {
                        if (string.IsNullOrEmpty(album.Artist)) album.Artist = artist.Name;
                        if (string.IsNullOrEmpty(album.ArtistId)) album.ArtistId = artist.Id;
                    }
                }

                var items = (includeAlbums ? albums : []).Select(_responseBuilder.ConvertAlbumToJellyfinItem)
                    .Concat((includeTracks ? tracks : []).Select(_responseBuilder.ConvertSongToJellyfinItem))
                    .ToList();
                var startIndex = GetRequestedStartIndex();
                var limit = int.TryParse(Request.Query["Limit"], out var parsedLimit) && parsedLimit >= 0
                    ? parsedLimit
                    : int.MaxValue;
                _logger.LogDebug(
                    "Found {AlbumCount} albums and {TrackCount} tracks for artist {ArtistName}",
                    albums.Count,
                    tracks.Count,
                    artist?.Name ?? "unknown");
                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = items.Skip(startIndex).Take(limit).ToList(),
                    TotalRecordCount = items.Count,
                    StartIndex = startIndex
                });
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

            var externalTask = _metadataService.SearchArtistsAsync(
                cleanQuery,
                limit,
                HttpContext.RequestAborted);

            await Task.WhenAll(jellyfinTask, externalTask);

            var (jellyfinResult, _) = await jellyfinTask;
            var externalArtists = await externalTask;

            _logger.LogDebug("Artist search results: Jellyfin={JellyfinCount}, External={ExternalCount}",
                jellyfinResult != null ? "found" : "null", externalArtists.Count);

            var artistItems = new List<Dictionary<string, object?>>();
            if (jellyfinResult != null && jellyfinResult.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    artistItems.Add(item.Deserialize<Dictionary<string, object?>>() ?? []);
                }
            }

            artistItems.AddRange(externalArtists.Select(_responseBuilder.ConvertArtistToJellyfinItem));
            _logger.LogDebug("Returning {Count} total artists (local + external, no deduplication)", artistItems.Count);

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
            var artist = await GetProviderArtistAsync(provider!, externalId!);
            if (artist == null)
            {
                return _responseBuilder.CreateError(404, "Artist not found");
            }

            var albums = await GetProviderArtistAlbumsAsync(provider!, externalId!);
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
        return HandleProxyResponse(jellyfinArtist, statusCode);
    }

    #endregion

    #region Images

    /// <summary>
    /// Gets the primary image for an item.
    /// </summary>
    [HttpGet("Items/{itemId}/Images/{imageType}")]
    [HttpGet("Items/{itemId}/Images/{imageType}/{imageIndex}")]
    [HttpHead("Items/{itemId}/Images/{imageType}")]
    [HttpHead("Items/{itemId}/Images/{imageType}/{imageIndex}")]
    public async Task<IActionResult> GetImage(
        string itemId,
        string imageType,
        int imageIndex = 0,
        [FromQuery] int? maxWidth = null,
        [FromQuery] int? maxHeight = null,
        [FromQuery(Name = "tag")] string? tag = null,
        [FromQuery(Name = "format")] string? requestedFormat = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return NotFound();
        }

        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(itemId))
        {
            var sourceId = await _virtualPlaylistProtocolAdapter.GetImageSourceIdAsync(
                HttpContext.GetProtocolExecutionContext(), itemId, HttpContext.RequestAborted);
            if (sourceId == null) return NotFound();
            return PlaylistIdHelper.IsExternalPlaylist(sourceId)
                ? await GetPlaylistImage(sourceId, maxWidth, maxHeight, requestedFormat)
                : await RelayCurrentRequestToPlaylistTargetAsync(
                    Request.Path.Value!, itemId, sourceId);
        }

        // Check for external playlist
        if (PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            return await GetPlaylistImage(itemId, maxWidth, maxHeight, requestedFormat);
        }

        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(itemId);

        if (!isExternal)
        {
            return await RelayCurrentRequestAsync(Request.Path.Value!.TrimStart('/'));
        }

        try
        {
            var asset = await ResolveExternalImageAsync(
                provider!, type!, externalId!, retryTransientFailures: true,
                maxWidth, maxHeight);
            if (asset == null)
            {
                return GetPlaceholderImage();
            }
            return CreateFormattedImageResponse(asset, requestedFormat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch cover art for {Provider}/{ExternalId}", provider, externalId);
            return GetPlaceholderImage();
        }
    }

    [HttpGet("Items/{itemId}/Images/{imageType}/{imageIndex}/{tag}/{format}/{maxWidth}/{maxHeight}/{percentPlayed}/{unplayedCount}")]
    [HttpHead("Items/{itemId}/Images/{imageType}/{imageIndex}/{tag}/{format}/{maxWidth}/{maxHeight}/{percentPlayed}/{unplayedCount}")]
    public Task<IActionResult> GetImageByPath(
        string itemId,
        string imageType,
        int imageIndex,
        string tag,
        string format,
        int maxWidth,
        int maxHeight,
        double percentPlayed,
        int unplayedCount) =>
        GetImage(itemId, imageType, imageIndex, maxWidth, maxHeight, tag, format);

    private async Task<ResolvedMediaAsset?> ResolveExternalImageAsync(
        string provider,
        string resourceKind,
        string resourceId,
        bool retryTransientFailures = false,
        int? width = null,
        int? height = null)
    {
        var actor = HttpContext.GetProtocolExecutionContext()?.Actor;
        return await _mediaAssets.ResolveAsync(
            new MediaAssetIdentity(
                actor?.TenantId,
                actor?.EffectiveUserId,
                null,
                provider,
                resourceKind,
                resourceId,
                Width: width > 0 ? width : null,
                Height: height > 0 ? height : null),
            async token =>
            {
                async Task<MediaAssetSource?> Fetch()
                {
                    var coverUrl = resourceKind switch
                    {
                        "artist" => (await GetProviderArtistForImageAsync(
                            provider, resourceId, token))?.ImageUrl,
                        "album" => (await GetProviderAlbumForImageAsync(
                            provider, resourceId, token))?.CoverArtUrl,
                        "song" => (await GetProviderSongForImageAsync(
                            provider, resourceId, token))?.CoverArtUrl,
                        "playlist" => (await GetProviderPlaylistForImageAsync(
                            provider, resourceId, token))?.CoverUrl,
                        _ => null
                    };
                    if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                            coverUrl, out var coverUri, out var validationReason) ||
                        coverUri == null)
                    {
                        _logger.LogDebug(
                            "No usable external image URL for {Type} {Provider}/{ExternalId}: {Reason}",
                            resourceKind,
                            provider,
                            resourceId,
                            validationReason);
                        return null;
                    }

                    coverUri = SelectExternalArtworkVariant(
                        coverUri, provider, width, height);
                    using var response = await _proxyService.HttpClient.GetAsync(coverUri, token);
                    if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or
                        System.Net.HttpStatusCode.ServiceUnavailable)
                        throw new HttpRequestException(
                            $"Transient error: {response.StatusCode}", null, response.StatusCode);
                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    if (!response.IsSuccessStatusCode ||
                        response.Content.Headers.ContentLength > MaximumArtworkBytes ||
                        contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == false)
                        return null;
                    await response.Content.LoadIntoBufferAsync(MaximumArtworkBytes, token);
                    var bytes = await response.Content.ReadAsByteArrayAsync(token);
                    return new MediaAssetSource(
                        bytes,
                        contentType ?? DetectImageContentType(bytes),
                        response.Headers.ETag?.Tag,
                        response.Content.Headers.LastModified);
                }

                return retryTransientFailures
                    ? await RetryHelper.RetryWithBackoffAsync(
                        Fetch,
                        _logger,
                        maxRetries: 3,
                        initialDelayMs: 500,
                        cancellationToken: HttpContext.RequestAborted)
                    : await Fetch();
            },
            MaximumArtworkBytes,
            HttpContext.RequestAborted);
    }

    internal static Uri SelectExternalArtworkVariant(
        Uri coverUri,
        string provider,
        int? width,
        int? height)
    {
        var size = width > 0 && height > 0
            ? Math.Min(width.Value, height.Value)
            : width > 0
                ? width.Value
                : height > 0
                    ? height.Value
                    : 0;
        if (size == 0 ||
            !provider.StartsWith("apple", StringComparison.OrdinalIgnoreCase) ||
            !coverUri.Host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase))
            return coverUri;

        size = Math.Min(size, 3000);
        var path = Regex.Replace(
            coverUri.AbsolutePath,
            @"/\d+x\d+bb(?=\.[^/]+$)",
            $"/{size}x{size}bb",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(50));
        return path == coverUri.AbsolutePath
            ? coverUri
            : new UriBuilder(coverUri) { Path = path }.Uri;
    }

    private IActionResult CreateFormattedImageResponse(
        ResolvedMediaAsset asset,
        string? requestedFormat)
    {
        var format = requestedFormat?.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            "png" => SKEncodedImageFormat.Png,
            "webp" => SKEncodedImageFormat.Webp,
            _ => (SKEncodedImageFormat?)null
        };
        if (format == null)
            return CreateConditionalImageResponse(asset.Bytes, asset.ContentType);
        var contentType = format == SKEncodedImageFormat.Jpeg
            ? "image/jpeg"
            : format == SKEncodedImageFormat.Png
                ? "image/png"
                : "image/webp";
        if (HasEncodedImageSignature(asset.Bytes, format.Value))
            return CreateConditionalImageResponse(asset.Bytes, contentType);

        try
        {
            using var data = SKData.CreateCopy(asset.Bytes);
            using var codec = SKCodec.Create(data);
            var info = codec?.Info;
            if (info is not { Width: > 0, Height: > 0 })
                return StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if ((long)info.Value.Width * info.Value.Height > MediaAssetResolver.MaximumDecodedPixels)
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var bitmap = SKBitmap.Decode(codec);
            using var image = bitmap == null ? null : SKImage.FromBitmap(bitmap);
            using var encoded = image?.Encode(format.Value, 90);
            var bytes = encoded?.ToArray();
            if (bytes is { Length: > 0 } && bytes.Length <= MaximumArtworkBytes)
            {
                return CreateConditionalImageResponse(bytes, contentType);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Unable to encode requested Jellyfin image format {Format}", requestedFormat);
        }

        return StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }

    private static bool HasEncodedImageSignature(
        ReadOnlySpan<byte> bytes,
        SKEncodedImageFormat format) =>
        format switch
        {
            SKEncodedImageFormat.Jpeg => bytes.Length >= 3 &&
                                         bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            SKEncodedImageFormat.Png => bytes.Length >= 8 &&
                                        bytes[..8].SequenceEqual(
                                            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            SKEncodedImageFormat.Webp => bytes.Length >= 12 &&
                                         bytes[..4].SequenceEqual("RIFF"u8) &&
                                         bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };

    private static string DetectImageContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        // Extension and legacy metadata fixtures may not expose a reliable upstream
        // content type. JPEG remains the compatibility fallback used by Jellyfin.
        return "image/jpeg";
    }

    private IActionResult GetPlaceholderImage() =>
        CreateConditionalImageResponse(PlaceholderImageBytes, "image/png");

    private IActionResult CreateConditionalImageResponse(byte[] imageBytes, string contentType)
    {
        var response = _imageProtocolAdapter.Shape(
            imageBytes,
            contentType,
            Request.Headers);
        Response.Headers.ETag = response.ETag;

        if (response.StatusCode == StatusCodes.Status304NotModified)
        {
            return StatusCode(response.StatusCode);
        }

        return File(response.Body!, response.ContentType);
    }

    private async Task<string?> ResolveCurrentSpotifyPlaylistImageTagAsync(string itemId, string imageType)
    {
        try
        {
            var (itemResult, statusCode) = await _proxyService.GetJsonAsync($"Items/{itemId}", null, Request.Headers);
            if (itemResult == null || statusCode != 200)
            {
                _logger.LogDebug(
                    "Skipping Jellyfin {ImageType} image tag resolution for Spotify playlist {PlaylistId}: upstream returned {StatusCode}",
                    imageType,
                    itemId,
                    statusCode);
                return null;
            }

            using var itemDocument = itemResult;
            var imageTag = ExtractImageTag(itemDocument.RootElement, imageType);

            if (!string.IsNullOrWhiteSpace(imageTag))
            {
                _logger.LogDebug(
                    "Resolved current Jellyfin {ImageType} image tag for Spotify playlist {PlaylistId}: {ImageTag}",
                    imageType,
                    itemId,
                    imageTag);
            }

            return imageTag;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Failed to resolve current Jellyfin {ImageType} image tag for Spotify playlist {PlaylistId}",
                imageType,
                itemId);
            return null;
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

        _logger.LogDebug("MarkFavorite called: userId={UserId}, itemId={ItemId}, route={Route}",
            userId, itemId, Request.Path);

        // Check if this is an external playlist - trigger download
        if (PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            if (CanRunOptionalUserScopedWork())
            {
                await RecordFavoriteEventSafelyAsync(itemId, FavoriteOperation.Favorite);
            }

            return CreateProtocolResponse(_interactionProtocolAdapter.ShapeFavorite(itemId, true));
        }

        // Check if this is an external song/album
        var (isExternal, _, _, _) =
            _localLibraryService.ParseExternalId(itemId);
        if (isExternal)
        {
            if (CanRunOptionalUserScopedWork())
            {
                await RecordFavoriteEventSafelyAsync(itemId, FavoriteOperation.Favorite);
            }

            return CreateProtocolResponse(_interactionProtocolAdapter.ShapeFavorite(itemId, true));
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

        if (statusCode is >= 200 and < 300 && CanRunOptionalUserScopedWork())
        {
            await RecordFavoriteEventSafelyAsync(itemId, FavoriteOperation.Favorite);
        }

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

        // External favorite state is logical only. Managed-file removal is a separate explicit action.
        var (isExternal, _, _, _) =
            _localLibraryService.ParseExternalId(itemId);
        if (isExternal || PlaylistIdHelper.IsExternalPlaylist(itemId))
        {
            if (CanRunOptionalUserScopedWork())
                await RecordFavoriteEventSafelyAsync(itemId, FavoriteOperation.Unfavorite);
            _logger.LogInformation(
                "Unfavorited external item {ItemId}; managed files were preserved",
                itemId);

            return CreateProtocolResponse(_interactionProtocolAdapter.ShapeFavorite(itemId, false));
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

        if (statusCode is >= 200 and < 300 && CanRunOptionalUserScopedWork())
        {
            await RecordFavoriteEventSafelyAsync(itemId, FavoriteOperation.Unfavorite);
        }

        return HandleProxyResponse(result, statusCode);
    }

    private async Task RecordFavoriteEventSafelyAsync(string itemId, FavoriteOperation operation)
    {
        if (_favoriteActions == null) return;
        try
        {
            var execution = HttpContext.RequireProtocolExecutionContext();
            var sourceRevision = Request.Headers["Idempotency-Key"].FirstOrDefault()
                ?? Request.Headers["X-Allstarr-Source-Revision"].FirstOrDefault()
                ?? "protocol-state-v1";
            await _favoriteActions.RecordAsync(
                new FavoriteMutationRequest(execution, itemId, operation, sourceRevision),
                HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backend mutation already succeeded. Optional work is observable through the durable system
            // when recorded, but must never rewrite the backend's favorite response.
            _logger.LogWarning("Favorite workflow recording failed ({ExceptionType})", ex.GetType().Name);
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
                    TotalRecordCount = 0,
                    StartIndex = 0
                });
            }

            try
            {
                // Get the original song to find similar content
                var song = await GetProviderSongAsync(provider!, externalId!);
                if (song == null)
                {
                    return _responseBuilder.CreateJsonResponse(new
                    {
                        Items = Array.Empty<object>(),
                        TotalRecordCount = 0,
                        StartIndex = 0
                    });
                }

                // Search for similar songs using artist and genre
                var searchQuery = $"{song.Artist}";
                var searchResult = await SearchProviderSongsAsync(
                    provider!, searchQuery, limit, HttpContext.RequestAborted);

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
                    TotalRecordCount = similarSongs.Count,
                    StartIndex = 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get similar items for external song {ItemId}", itemId);
                return _responseBuilder.CreateJsonResponse(new
                {
                    Items = Array.Empty<object>(),
                    TotalRecordCount = 0,
                    StartIndex = 0
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
    [HttpGet("Albums/{itemId}/InstantMix")]
    [HttpGet("Artists/{itemId}/InstantMix")]
    [HttpGet("MusicGenres/{itemId}/InstantMix")]
    [HttpGet("Playlists/{itemId}/InstantMix")]
    public async Task<IActionResult> GetInstantMix(
        string itemId,
        [FromQuery] int limit = 50,
        [FromQuery] string? fields = null,
        [FromQuery] string? userId = null)
    {
        if (JellyfinMusicEndpointPolicy.IsSynthesizedPlaylistId(itemId))
        {
            return await RelaySynthesizedPlaylistTargetAsync(
                Request.Path.Value!.TrimStart('/'),
                itemId);
        }

        var sonicMix = await TryGetSonicInstantMixAsync(itemId, limit, fields);
        if (sonicMix != null) return sonicMix;

        var (isExternal, provider, resourceType, externalId) =
            _localLibraryService.ParseExternalId(itemId);

        if (isExternal)
        {
            if (resourceType is not ("song" or "album" or "artist"))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!CanRunOptionalUserScopedWork())
            {
                return CreateProtocolResponse(_interactionProtocolAdapter.ShapeInstantMix([]));
            }

            try
            {
                var mixSongs = new List<Song>();
                string? artistName;
                if (resourceType?.Equals("album", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var album = await GetProviderAlbumAsync(
                        provider!, externalId!, HttpContext.RequestAborted);
                    if (album == null) return CreateProtocolResponse(
                        _interactionProtocolAdapter.ShapeInstantMix([]));
                    mixSongs.AddRange(album.Songs);
                    artistName = album.Artist;
                }
                else if (resourceType?.Equals("artist", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var artist = await GetProviderArtistAsync(
                        provider!, externalId!, HttpContext.RequestAborted);
                    if (artist == null) return CreateProtocolResponse(
                        _interactionProtocolAdapter.ShapeInstantMix([]));
                    artistName = artist.Name;
                    var albums = await GetProviderArtistAlbumsAsync(
                        provider!, externalId!, HttpContext.RequestAborted);
                    foreach (var album in albums.Take(3))
                    {
                        if (string.IsNullOrWhiteSpace(album.ExternalId)) continue;
                        var fullAlbum = await GetProviderAlbumAsync(
                            provider!, album.ExternalId, HttpContext.RequestAborted);
                        if (fullAlbum != null) mixSongs.AddRange(fullAlbum.Songs);
                        if (mixSongs.Count >= limit) break;
                    }
                }
                else
                {
                    var song = await GetProviderSongAsync(
                        provider!, externalId!, HttpContext.RequestAborted);
                    if (song == null) return CreateProtocolResponse(
                        _interactionProtocolAdapter.ShapeInstantMix([]));
                    artistName = song.Artist;
                    if (!string.IsNullOrEmpty(song.ExternalProvider) &&
                        !string.IsNullOrEmpty(song.ArtistId))
                    {
                        var artistExternalId = song.ArtistId.Replace(
                            $"ext-{song.ExternalProvider}-artist-", "");
                        var albums = await GetProviderArtistAlbumsAsync(
                            song.ExternalProvider,
                            artistExternalId,
                            HttpContext.RequestAborted);
                        foreach (var album in albums.Take(3))
                        {
                            if (string.IsNullOrWhiteSpace(album.ExternalId)) continue;
                            var fullAlbum = await GetProviderAlbumAsync(
                                song.ExternalProvider,
                                album.ExternalId,
                                HttpContext.RequestAborted);
                            if (fullAlbum != null) mixSongs.AddRange(fullAlbum.Songs);
                            if (mixSongs.Count >= limit) break;
                        }
                    }
                }

                if (mixSongs.Count < limit && !string.IsNullOrWhiteSpace(artistName))
                {
                    var searchResult = await SearchProviderSongsAsync(
                        provider!, artistName, limit, HttpContext.RequestAborted);
                    mixSongs.AddRange(searchResult.Where(song =>
                        mixSongs.All(existing => existing.Id != song.Id)));
                }

                // Keep the same seed stable across retries and process restarts.
                var shuffledMix = mixSongs
                    .Where(song => resourceType is "album" or "artist" ||
                                   song.Id != itemId && song.ExternalId != externalId)
                    .OrderBy(songItem => StableInstantMixOrder(itemId, songItem.Id), StringComparer.Ordinal)
                    .Take(limit)
                    .Select(s => _responseBuilder.ConvertSongToJellyfinItem(s))
                    .ToList();

                return CreateProtocolResponse(_interactionProtocolAdapter.ShapeInstantMix(shuffledMix));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create instant mix for external song {ItemId}", itemId);
                return CreateProtocolResponse(_interactionProtocolAdapter.ShapeInstantMix([]));
            }
        }

        // For local items, proxy using the same route shape and full query string from the client
        var endpoint = Request.Path.Value!.TrimStart('/');

        if (Request.QueryString.HasValue)
        {
            endpoint = $"{endpoint}{Request.QueryString.Value}";
        }

        var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);

        return HandleProxyResponse(result, statusCode);
    }

    [HttpGet("MusicGenres/InstantMix")]
    public Task<IActionResult> GetMusicGenreInstantMixById(
        [FromQuery] string id,
        [FromQuery] int limit = 50,
        [FromQuery] string? fields = null,
        [FromQuery] string? userId = null)
    {
        var (isExternal, _, _, _) = _localLibraryService.ParseExternalId(id);
        return isExternal || JellyfinMusicEndpointPolicy.IsSynthesizedPlaylistId(id)
            ? Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden))
            : GetInstantMix(id, limit, fields, userId);
    }

    [HttpGet("Artists/InstantMix", Order = 1)]
    public Task<IActionResult> GetArtistInstantMixById(
        [FromQuery] string id,
        [FromQuery] int limit = 50,
        [FromQuery] string? fields = null,
        [FromQuery] string? userId = null)
    {
        var (isExternal, _, resourceType, _) = _localLibraryService.ParseExternalId(id);
        return JellyfinMusicEndpointPolicy.IsSynthesizedPlaylistId(id) ||
               isExternal && resourceType != "artist"
            ? Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden))
            : GetInstantMix(id, limit, fields, userId);
    }

    private static string StableInstantMixOrder(string seedItemId, string candidateItemId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{seedItemId}\n{candidateItemId}")));

    private bool CanRunOptionalUserScopedWork() =>
        _interactionProtocolAdapter.CanRunOptionalUserWork(HttpContext.GetProtocolExecutionContext());

    private IActionResult CreateProtocolResponse(JellyfinProtocolResponse response) =>
        new ContentResult
        {
            StatusCode = response.StatusCode,
            ContentType = response.ContentType,
            Content = response.Body
        };

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

    private async Task<IActionResult> RelayCurrentRequestAsync(string path)
    {
        var endpoint = Request.QueryString.HasValue
            ? $"{path}{Request.QueryString.Value}"
            : path;
        var upstream = await _proxyService.SendPassthroughResponseAsync(
            Request,
            endpoint,
            HttpContext.RequestAborted);
        return new ProtocolRelayResponseResult(upstream);
    }

    private Task<IActionResult> RelayCurrentRequestToPlaylistTargetAsync(
        string path,
        string playlistId,
        string targetPlaylistId)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(segments, segment =>
            segment.Equals(playlistId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return Task.FromResult<IActionResult>(BadRequest());
        segments[index] = Uri.EscapeDataString(targetPlaylistId);
        return RelayCurrentRequestAsync(string.Join('/', segments));
    }

    private async Task<IActionResult> RelaySynthesizedPlaylistTargetAsync(
        string path,
        string playlistId)
    {
        if (!_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId))
        {
            return Conflict(new { error = "Playlist is read-only." });
        }

        var route = await _virtualPlaylistProtocolAdapter.ResolveMutationAsync(
            HttpContext.RequireProtocolExecutionContext(),
            playlistId,
            HttpContext.RequestAborted);
        if (route == null) return NotFound();
        if (!route.Writable || string.IsNullOrWhiteSpace(route.TargetPlaylistId))
        {
            return Conflict(new { error = "Playlist is read-only." });
        }

        return await RelayCurrentRequestToPlaylistTargetAsync(
            path, playlistId, route.TargetPlaylistId);
    }

    /// <summary>
    /// Catch-all endpoint that proxies unhandled requests to Jellyfin transparently.
    /// This route has the lowest priority and should only match requests that don't have SearchTerm.
    /// Blocks dangerous admin endpoints for security.
    /// </summary>
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", Route = "{**path}", Order = 100)]
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
        if (HttpMethods.IsGet(Request.Method) && !string.IsNullOrEmpty(playlistItemsRequestId))
        {
            if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistItemsRequestId))
            {
                return await GetPlaylistTracks(playlistItemsRequestId);
            }

            if (PlaylistIdHelper.IsExternalPlaylist(playlistItemsRequestId))
            {
                return await GetPlaylistTracks(playlistItemsRequestId);
            }

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
            return await ProxyMusicItemsResponseAsync(playlistItemsPath);
        }

        var playlistRequestId = GetPlaylistRequestId(path);
        if (!string.IsNullOrEmpty(playlistRequestId) &&
            JellyfinMusicEndpointPolicy.IsSynthesizedPlaylistId(playlistRequestId) &&
            JellyfinMusicEndpointPolicy.SupportsSynthesizedPlaylistRoute(Request, playlistRequestId))
        {
            return await RelaySynthesizedPlaylistTargetAsync(path, playlistRequestId);
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
            return await RelayCurrentRequestAsync(path);
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
            var needsPlaylistCountRewrite = HttpMethods.IsGet(Request.Method) &&
                _spotifySettings.Enabled &&
                Request.Query["IncludeItemTypes"].ToString()
                    .Contains("Playlist", StringComparison.OrdinalIgnoreCase);
            if (!needsPlaylistCountRewrite)
            {
                return await RelayCurrentRequestAsync(path);
            }

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
            }

            // Return the raw JSON element directly to avoid deserialization issues with simple types
            return new JsonResult(result.RootElement.Clone()) { StatusCode = statusCode };
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

}
