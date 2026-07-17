using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.Subsonic;
using allstarr.Filters;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Protocols;
using allstarr.Core.Favorites;
using allstarr.Core.Playback;
using allstarr.Core.Capabilities;

namespace allstarr.Controllers;

[ApiController]
[Route("")]
[ServiceFilter(typeof(SubsonicAuthFilter), Order = int.MinValue)]
[ServiceFilter(typeof(ProtocolExecutionContextFilter), Order = int.MinValue + 1)]
public class SubsonicController : ControllerBase
{
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IMusicMetadataService _metadataService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly IDownloadService _downloadService;
    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicModelMapper _modelMapper;
    private readonly SubsonicProxyService _proxyService;
    private readonly SubsonicLyricsProtocolAdapter _lyricsProtocolAdapter;
    private readonly SubsonicRelayProtocolAdapter _relayProtocolAdapter;
    private readonly SubsonicSearchProtocolAdapter _searchProtocolAdapter;
    private readonly SubsonicScrobbleProtocolAdapter _scrobbleProtocolAdapter;
    private readonly SubsonicVirtualPlaylistProtocolAdapter _virtualPlaylistProtocolAdapter;
    private readonly PlaylistSyncService? _playlistSyncService;
    private readonly RedisCacheService _cache;
    private readonly ILogger<SubsonicController> _logger;
    private readonly IFavoriteActionPipeline? _favoriteActions;
    private readonly IPlaybackSignalPipeline? _playbackSignals;
    private readonly IProtocolProviderGateway? _providerGateway;
    private readonly ProtocolStreamingResponseAdapter? _streamingResponseAdapter;

    public SubsonicController(
        IOptions<SubsonicSettings> subsonicSettings,
        IMusicMetadataService metadataService,
        ILocalLibraryService localLibraryService,
        IDownloadService downloadService,
        SubsonicRequestParser requestParser,
        SubsonicResponseBuilder responseBuilder,
        SubsonicModelMapper modelMapper,
        SubsonicProxyService proxyService,
        SubsonicLyricsProtocolAdapter lyricsProtocolAdapter,
        SubsonicRelayProtocolAdapter relayProtocolAdapter,
        SubsonicSearchProtocolAdapter searchProtocolAdapter,
        SubsonicScrobbleProtocolAdapter scrobbleProtocolAdapter,
        SubsonicVirtualPlaylistProtocolAdapter virtualPlaylistProtocolAdapter,
        RedisCacheService cache,
        ILogger<SubsonicController> logger,
        PlaylistSyncService? playlistSyncService = null,
        IFavoriteActionPipeline? favoriteActions = null,
        IPlaybackSignalPipeline? playbackSignals = null,
        IProtocolProviderGateway? providerGateway = null,
        ProtocolStreamingResponseAdapter? streamingResponseAdapter = null)
    {
        _subsonicSettings = subsonicSettings.Value;
        _metadataService = metadataService;
        _localLibraryService = localLibraryService;
        _downloadService = downloadService;
        _requestParser = requestParser;
        _responseBuilder = responseBuilder;
        _modelMapper = modelMapper;
        _proxyService = proxyService;
        _lyricsProtocolAdapter = lyricsProtocolAdapter;
        _relayProtocolAdapter = relayProtocolAdapter;
        _searchProtocolAdapter = searchProtocolAdapter;
        _scrobbleProtocolAdapter = scrobbleProtocolAdapter;
        _virtualPlaylistProtocolAdapter = virtualPlaylistProtocolAdapter;
        _playlistSyncService = playlistSyncService;
        _cache = cache;
        _logger = logger;
        _favoriteActions = favoriteActions;
        _playbackSignals = playbackSignals;
        _providerGateway = providerGateway;
        _streamingResponseAdapter = streamingResponseAdapter;

        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url))
        {
            throw new Exception("Error: Environment variable SUBSONIC_URL is not set.");
        }
    }

    [HttpGet, HttpPost]
    [Route("rest/getLyricsBySongId")]
    [Route("rest/getLyricsBySongId.view")]
    public async Task<IActionResult> GetLyricsBySongId()
    {
        var parameters = await ExtractAllParameters();
        return await _lyricsProtocolAdapter.GetLyricsBySongIdAsync(
            parameters,
            HttpContext.RequestAborted);
    }

    // Extract all parameters (query + body)
    private async Task<SubsonicRequestParameters> ExtractAllParameters()
    {
        if (HttpContext.Items.TryGetValue(SubsonicAuthFilter.RequestParametersItemKey, out var value) &&
            value is SubsonicRequestParameters verifiedParameters)
        {
            return verifiedParameters;
        }

        return await _requestParser.ExtractAllParametersAsync(Request);
    }

    private ProtocolExecutionContext CurrentProtocolContext =>
        HttpContext.Items.TryGetValue(ProtocolExecutionContextFactory.HttpContextItemKey, out var value) &&
        value is ProtocolExecutionContext context
            ? context
            : throw new InvalidOperationException("Authenticated Subsonic action has no protocol context.");

    private Task<Song?> GetProviderSongAsync(string provider, string externalId) => _providerGateway != null
        ? _providerGateway.GetSongAsync(CurrentProtocolContext, provider, externalId)
        : _metadataService.GetSongAsync(provider, externalId, HttpContext.RequestAborted);

    private Task<Album?> GetProviderAlbumAsync(string provider, string externalId) => _providerGateway != null
        ? _providerGateway.GetAlbumAsync(CurrentProtocolContext, provider, externalId)
        : _metadataService.GetAlbumAsync(provider, externalId, HttpContext.RequestAborted);

    private Task<Artist?> GetProviderArtistAsync(string provider, string externalId) => _providerGateway != null
        ? _providerGateway.GetArtistAsync(CurrentProtocolContext, provider, externalId)
        : _metadataService.GetArtistAsync(provider, externalId, HttpContext.RequestAborted);

    /// <summary>
    /// Merges local and external search results.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/search3")]
    [Route("rest/search3.view")]
    public async Task<IActionResult> Search3()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var window = _searchProtocolAdapter.Parse(parameters, CurrentProtocolContext);
        var cleanQuery = window.Query;

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            try
            {
                var result = await _proxyService.RelayRawAsync(
                    "rest/search3",
                    parameters,
                    HttpContext.RequestAborted);
                return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Subsonic empty-search relay failed ({ExceptionType})",
                    ex.GetType().Name);
                return StatusCode(StatusCodes.Status502BadGateway);
            }
        }

        var subsonicTask = _proxyService.RelaySafeAsync("rest/search3", parameters);
        var externalTask = _providerGateway != null
            ? _providerGateway.SearchAsync(
                CurrentProtocolContext,
                cleanQuery,
                window.SongFetchCount,
                window.AlbumFetchCount,
                window.ArtistFetchCount)
            : _metadataService.SearchAllAsync(
                cleanQuery,
                window.SongFetchCount,
                window.AlbumFetchCount,
                window.ArtistFetchCount,
                HttpContext.RequestAborted);

        // Search playlists if enabled
        Task<List<ExternalPlaylist>> playlistTask = _subsonicSettings.EnableExternalPlaylists
            ? _providerGateway != null
                ? _providerGateway.SearchPlaylistsAsync(
                    CurrentProtocolContext,
                    cleanQuery,
                    window.AlbumFetchCount)
                : _metadataService.SearchPlaylistsAsync(
                    cleanQuery,
                    window.AlbumFetchCount,
                    HttpContext.RequestAborted)
            : Task.FromResult(new List<ExternalPlaylist>());

        await Task.WhenAll(subsonicTask, externalTask, playlistTask);

        var subsonicResult = await subsonicTask;
        var externalResult = _searchProtocolAdapter.ApplyWindow(await externalTask, window);
        var playlistResult = _searchProtocolAdapter.ApplyAlbumWindow(await playlistTask, window);

        return MergeSearchResults(subsonicResult, externalResult, playlistResult, format);
    }

    /// <summary>
    /// Downloads on-the-fly if needed.
    /// </summary>
    [AcceptVerbs("GET", "POST", "HEAD")]
    [Route("rest/stream")]
    [Route("rest/stream.view")]
    public async Task<IActionResult> Stream()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");

        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Missing id parameter" });
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (!isExternal)
        {
            return await _proxyService.RelayStreamAsync(parameters, HttpContext.RequestAborted);
        }

        var localPath = await _localLibraryService.GetLocalPathForExternalSongAsync(provider!, externalId!);

        if (localPath != null && System.IO.File.Exists(localPath))
        {
            // Update last write time for cache cleanup (extends cache lifetime)
            try
            {
                System.IO.File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update last write time for {Path}", localPath);
            }

            var stream = System.IO.File.OpenRead(localPath);
            return File(stream, GetContentType(localPath), enableRangeProcessing: true);
        }

        if (_providerGateway != null && _streamingResponseAdapter != null)
        {
            try
            {
                var routed = await _providerGateway.OpenStreamAsync(
                    CurrentProtocolContext,
                    provider!,
                    externalId!,
                    ProviderAudioQuality.Any,
                    Request.Headers.Range.ToString() is { Length: > 0 } range ? range : null);
                if (routed != null)
                {
                    if (!routed.Response.IsSuccessStatusCode)
                    {
                        var status = (int)routed.Response.StatusCode;
                        routed.Response.Dispose();
                        return StatusCode(status);
                    }
                    return await _streamingResponseAdapter.CreateAsync(
                        HttpContext,
                        routed.Response,
                        HttpContext.RequestAborted,
                        enableRangeProcessing: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Typed provider stream route failed for {Provider}", provider);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "External stream failed" });
            }
        }

        try
        {
            var downloadStream = await _downloadService.DownloadAndStreamAsync(provider!, externalId!, cancellationToken: HttpContext.RequestAborted);

            var contentType = "audio/mpeg";
            if (downloadStream is FileStream fs)
            {
                contentType = GetContentType(fs.Name);
            }

            return File(downloadStream, contentType, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            if (HttpContext.RequestAborted.IsCancellationRequested && ex is OperationCanceledException)
            {
                _logger.LogInformation("Client aborted external Subsonic stream request for {Id}", id);
                return StatusCode(499);
            }

            if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue)
            {
                var statusCode = httpRequestException.StatusCode == System.Net.HttpStatusCode.NotFound ? 404 : 503;
                _logger.LogError(ex, "Failed to stream external Subsonic item {Id}: responding {StatusCode}; upstream returned {UpstreamStatus}",
                    id, statusCode, (int)httpRequestException.StatusCode.Value);
                return StatusCode(statusCode, new { error = statusCode == 404 ? "External track not found" : "External provider unavailable" });
            }

            if (ex is TimeoutException || ex is TaskCanceledException)
            {
                _logger.LogError(ex, "Timed out streaming external Subsonic item {Id}", id);
                return StatusCode(504, new { error = "External provider timed out" });
            }

            if (ex is InvalidOperationException invalidOperationException &&
                invalidOperationException.Message.Contains("endpoints", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(ex, "No healthy endpoints available for external Subsonic item {Id}", id);
                return StatusCode(503, new { error = "External provider has no healthy endpoints" });
            }

            _logger.LogError(ex, "Failed to stream external Subsonic item {Id}", id);
            return StatusCode(502, new { error = "External stream failed" });
        }
    }

    /// <summary>
    /// Returns external song info if needed.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getSong")]
    [Route("rest/getSong.view")]
    public async Task<IActionResult> GetSong()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            return _responseBuilder.CreateError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (!isExternal)
        {
            var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getSong";
            var result = await _proxyService.RelayRawAsync(
                relayEndpoint,
                parameters,
                HttpContext.RequestAborted,
                Request.Headers);
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
        }

        var song = await GetProviderSongAsync(provider!, externalId!);

        if (song == null)
        {
            return _responseBuilder.CreateError(format, 70, "Song not found");
        }

        return _responseBuilder.CreateSongResponse(format, song);
    }

    /// <summary>
    /// Merges local and Deezer albums.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getArtist")]
    [Route("rest/getArtist.view")]
    public async Task<IActionResult> GetArtist()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            return _responseBuilder.CreateError(format, 10, "Missing id parameter");
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (isExternal)
        {
            var artist = await GetProviderArtistAsync(provider!, externalId!);
            if (artist == null)
            {
                return _responseBuilder.CreateError(format, 70, "Artist not found");
            }

            var albums = await _metadataService.GetArtistAlbumsAsync(provider!, externalId!);

            // Fill artist info for each album (Deezer API doesn't include it in artist/albums endpoint)
            foreach (var album in albums)
            {
                if (string.IsNullOrEmpty(album.Artist))
                {
                    album.Artist = artist.Name;
                }
                if (string.IsNullOrEmpty(album.ArtistId))
                {
                    album.ArtistId = artist.Id;
                }
            }

            return _responseBuilder.CreateArtistResponse(format, artist, albums);
        }

        var navidromeResult = await _proxyService.RelaySafeAsync("rest/getArtist", parameters);

        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return _responseBuilder.CreateError(format, 70, "Artist not found");
        }

        var navidromeContent = Encoding.UTF8.GetString(navidromeResult.Body);
        string artistName = "";
        string localArtistId = id; // Keep the local artist ID for merged albums
        var localAlbums = new List<object>();
        object? artistData = null;

        if (format == "json" || navidromeResult.ContentType?.Contains("json") == true)
        {
            var jsonDoc = JsonDocument.Parse(navidromeContent);
            if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                response.TryGetProperty("artist", out var artistElement))
            {
                artistName = artistElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                artistData = _responseBuilder.ConvertSubsonicJsonElement(artistElement, true);

                if (artistElement.TryGetProperty("album", out var albums))
                {
                    foreach (var album in albums.EnumerateArray())
                    {
                        localAlbums.Add(_responseBuilder.ConvertSubsonicJsonElement(album, true));
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(artistName) || artistData == null)
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
        }

        var deezerArtists = await _metadataService.SearchArtistsAsync(artistName, 1);
        var deezerAlbums = new List<Album>();

        if (deezerArtists.Count > 0)
        {
            var deezerArtist = deezerArtists[0];
            if (deezerArtist.Name.Equals(artistName, StringComparison.OrdinalIgnoreCase))
            {
                deezerAlbums = await _metadataService.GetArtistAlbumsAsync("deezer", deezerArtist.ExternalId!);

                // Fill artist info for each album (Deezer API doesn't include it in artist/albums endpoint)
                // Use local artist ID and name so albums link back to the local artist
                foreach (var album in deezerAlbums)
                {
                    if (string.IsNullOrEmpty(album.Artist))
                    {
                        album.Artist = artistName;
                    }
                    if (string.IsNullOrEmpty(album.ArtistId))
                    {
                        album.ArtistId = localArtistId;
                    }
                }
            }
        }

        var localAlbumNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in localAlbums)
        {
            if (album is Dictionary<string, object> dict && dict.TryGetValue("name", out var nameObj))
            {
                localAlbumNames.Add(nameObj?.ToString() ?? "");
            }
        }

        var mergedAlbums = localAlbums.ToList();
        foreach (var deezerAlbum in deezerAlbums)
        {
            if (!localAlbumNames.Contains(deezerAlbum.Title))
            {
                mergedAlbums.Add(_responseBuilder.ConvertAlbumToJson(deezerAlbum));
            }
        }

        if (artistData is Dictionary<string, object> artistDict)
        {
            artistDict["album"] = mergedAlbums;
            artistDict["albumCount"] = mergedAlbums.Count;
        }

        return _responseBuilder.CreateJsonResponse(new
        {
            status = "ok",
            version = "1.16.1",
            artist = artistData
        });
    }

    /// <summary>
    /// Enriches local albums with Deezer songs.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getAlbum")]
    [Route("rest/getAlbum.view")]
    public async Task<IActionResult> GetAlbum()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            return _responseBuilder.CreateError(format, 10, "Missing id parameter");
        }

        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(id))
        {
            return await _virtualPlaylistProtocolAdapter.ReadAsync(
                       CurrentProtocolContext, id, format, HttpContext.RequestAborted)
                   ?? _responseBuilder.CreateError(format, 70, "Playlist not found");
        }

        // Check if this is an external playlist
        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            try
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

                // Get playlist metadata
                var playlist = _providerGateway != null
                    ? await _providerGateway.GetPlaylistAsync(CurrentProtocolContext, provider, externalId)
                    : await _metadataService.GetPlaylistAsync(provider, externalId);
                if (playlist == null)
                {
                    return _responseBuilder.CreateError(format, 70, "Playlist not found");
                }

                // Get playlist tracks
                var tracks = _providerGateway != null
                    ? await _providerGateway.GetPlaylistTracksAsync(CurrentProtocolContext, provider, externalId)
                    : await _metadataService.GetPlaylistTracksAsync(provider, externalId);

                // Add all tracks to playlist cache so when they're played, we know they belong to this playlist
                if (_playlistSyncService != null)
                {
                    foreach (var track in tracks)
                    {
                        if (!string.IsNullOrEmpty(track.ExternalId))
                        {
                            var trackId = $"ext-{provider}-{track.ExternalId}";
                            _playlistSyncService.AddTrackToPlaylistCache(trackId, id);
                        }
                    }

                    _logger.LogDebug("Added {TrackCount} tracks to playlist cache for {PlaylistId}", tracks.Count, id);
                }

                // Convert to album response (playlist as album)
                return _responseBuilder.CreatePlaylistAsAlbumResponse(format, playlist, tracks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting playlist {Id}", id);
                return _responseBuilder.CreateError(format, 70, "Playlist not found");
            }
        }

        var (isExternal, albumProvider, albumExternalId) = _localLibraryService.ParseSongId(id);

        if (isExternal)
        {
            var album = await GetProviderAlbumAsync(albumProvider!, albumExternalId!);

            if (album == null)
            {
                return _responseBuilder.CreateError(format, 70, "Album not found");
            }

            return _responseBuilder.CreateAlbumResponse(format, album);
        }

        var navidromeResult = await _proxyService.RelaySafeAsync("rest/getAlbum", parameters);

        if (!navidromeResult.Success || navidromeResult.Body == null)
        {
            return _responseBuilder.CreateError(format, 70, "Album not found");
        }

        var navidromeContent = Encoding.UTF8.GetString(navidromeResult.Body);
        string albumName = "";
        string artistName = "";
        var localSongs = new List<object>();
        object? albumData = null;

        if (format == "json" || navidromeResult.ContentType?.Contains("json") == true)
        {
            var jsonDoc = JsonDocument.Parse(navidromeContent);
            if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                response.TryGetProperty("album", out var albumElement))
            {
                albumName = albumElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                artistName = albumElement.TryGetProperty("artist", out var artist) ? artist.GetString() ?? "" : "";
                albumData = _responseBuilder.ConvertSubsonicJsonElement(albumElement, true);

                if (albumElement.TryGetProperty("song", out var songs))
                {
                    foreach (var song in songs.EnumerateArray())
                    {
                        localSongs.Add(_responseBuilder.ConvertSubsonicJsonElement(song, true));
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(albumName) || string.IsNullOrEmpty(artistName) || albumData == null)
        {
            return File(navidromeResult.Body, navidromeResult.ContentType ?? "application/json");
        }

        var searchQuery = $"{artistName} {albumName}";
        var deezerAlbums = await _metadataService.SearchAlbumsAsync(searchQuery, 5);
        Album? deezerAlbum = null;

        // Find matching album on Deezer (exact match first)
        foreach (var candidate in deezerAlbums)
        {
            if (candidate.Artist != null &&
                candidate.Artist.Equals(artistName, StringComparison.OrdinalIgnoreCase) &&
                candidate.Title.Equals(albumName, StringComparison.OrdinalIgnoreCase))
            {
                deezerAlbum = await GetProviderAlbumAsync("deezer", candidate.ExternalId!);
                break;
            }
        }

        // Fallback to fuzzy match
        if (deezerAlbum == null)
        {
            foreach (var candidate in deezerAlbums)
            {
                if (candidate.Artist != null &&
                    candidate.Artist.Contains(artistName, StringComparison.OrdinalIgnoreCase) &&
                    (candidate.Title.Contains(albumName, StringComparison.OrdinalIgnoreCase) ||
                     albumName.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    deezerAlbum = await GetProviderAlbumAsync("deezer", candidate.ExternalId!);
                    break;
                }
            }
        }

        if (deezerAlbum != null && deezerAlbum.Songs.Count > 0)
        {
            var localSongTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in localSongs)
            {
                if (song is Dictionary<string, object> dict && dict.TryGetValue("title", out var titleObj))
                {
                    localSongTitles.Add(titleObj?.ToString() ?? "");
                }
            }

            var mergedSongs = localSongs.ToList();
            foreach (var deezerSong in deezerAlbum.Songs)
            {
                if (!localSongTitles.Contains(deezerSong.Title))
                {
                    mergedSongs.Add(_responseBuilder.ConvertSongToJson(deezerSong));
                }
            }

            mergedSongs = mergedSongs
                .OrderBy(s => s is Dictionary<string, object> dict && dict.TryGetValue("track", out var track)
                    ? Convert.ToInt32(track)
                    : 0)
                .ToList();

            if (albumData is Dictionary<string, object> albumDict)
            {
                albumDict["song"] = mergedSongs;
                albumDict["songCount"] = mergedSongs.Count;

                var totalDuration = 0;
                foreach (var song in mergedSongs)
                {
                    if (song is Dictionary<string, object> dict && dict.TryGetValue("duration", out var dur))
                    {
                        totalDuration += Convert.ToInt32(dur);
                    }
                }
                albumDict["duration"] = totalDuration;
            }
        }

        return _responseBuilder.CreateJsonResponse(new
        {
            status = "ok",
            version = "1.16.1",
            album = albumData
        });
    }

    /// <summary>
    /// Reads an Allstarr virtual or hybrid playlist without writing it to the backend.
    /// Native backend playlist IDs remain transparent relay requests.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getPlaylist")]
    [Route("rest/getPlaylist.view")]
    public async Task<IActionResult> GetPlaylist()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", string.Empty);
        var format = parameters.GetValueOrDefault("f", "xml");
        if (_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(id))
        {
            return await _virtualPlaylistProtocolAdapter.ReadAsync(
                       CurrentProtocolContext, id, format, HttpContext.RequestAborted)
                   ?? _responseBuilder.CreateError(format, 70, "Playlist not found");
        }

        var endpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getPlaylist";
        var result = await _proxyService.RelayRawAsync(
            endpoint, parameters, HttpContext.RequestAborted, Request.Headers);
        return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
    }

    /// <summary>
    /// Proxies external covers. Uses type from ID to determine which API to call.
    /// Format: ext-{provider}-{type}-{id} (e.g., ext-deezer-artist-259, ext-deezer-album-96126)
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/getCoverArt")]
    [Route("rest/getCoverArt.view")]
    public async Task<IActionResult> GetCoverArt()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");

        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        // Check if this is a playlist cover art request
        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            try
            {
                // Check cache first (1 hour TTL for playlist images since they can change)
                var cacheKey = $"playlist:image:{id}";
                var cachedImage = await _cache.GetAsync<byte[]>(cacheKey);

                if (cachedImage != null)
                {
                    _logger.LogDebug("Serving cached playlist cover art for {Id}", id);
                    return File(cachedImage, "image/jpeg");
                }

                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);
                var playlist = _providerGateway != null
                    ? await _providerGateway.GetPlaylistAsync(CurrentProtocolContext, provider, externalId)
                    : await _metadataService.GetPlaylistAsync(provider, externalId);

                if (playlist == null || string.IsNullOrEmpty(playlist.CoverUrl))
                {
                    return NotFound();
                }

                // Download and return the cover image
                var imageResponse = await new HttpClient().GetAsync(playlist.CoverUrl);
                if (!imageResponse.IsSuccessStatusCode)
                {
                    return NotFound();
                }

                var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync();
                var contentType = imageResponse.Content.Headers.ContentType?.ToString() ?? "image/jpeg";

                // Cache for configurable duration (playlists can change)
                await _cache.SetAsync(cacheKey, imageBytes, CacheExtensions.PlaylistImagesTTL);
                _logger.LogDebug("Cached playlist cover art for {Id}", id);

                return File(imageBytes, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting playlist cover art for {Id}", id);
                return NotFound();
            }
        }

        var (isExternal, coverProvider, type, coverExternalId) = _localLibraryService.ParseExternalId(id);

        if (!isExternal)
        {
            try
            {
                var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getCoverArt";
                var result = await _proxyService.RelayRawAsync(
                    relayEndpoint,
                    parameters,
                    HttpContext.RequestAborted,
                    Request.Headers);
                return _relayProtocolAdapter.CreateResult(result, "image/jpeg");
            }
            catch
            {
                return NotFound();
            }
        }

        string? coverUrl = null;

        // Use type to determine which API to call first
        switch (type)
        {
            case "artist":
                var artist = await GetProviderArtistAsync(coverProvider!, coverExternalId!);
                if (artist?.ImageUrl != null)
                {
                    coverUrl = artist.ImageUrl;
                }
                break;

            case "album":
                var album = await GetProviderAlbumAsync(coverProvider!, coverExternalId!);
                if (album?.CoverArtUrl != null)
                {
                    coverUrl = album.CoverArtUrl;
                }
                break;

            case "song":
            default:
                // For songs, try to get from song first, then album
                var song = await GetProviderSongAsync(coverProvider!, coverExternalId!);
                if (song?.CoverArtUrl != null)
                {
                    coverUrl = song.CoverArtUrl;
                }
                else
                {
                    // Fallback: try album with same ID (legacy behavior)
                    var albumFallback = await GetProviderAlbumAsync(coverProvider!, coverExternalId!);
                    if (albumFallback?.CoverArtUrl != null)
                    {
                        coverUrl = albumFallback.CoverArtUrl;
                    }
                }
                break;
        }

        if (coverUrl != null)
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(coverUrl);
            if (response.IsSuccessStatusCode)
            {
                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                return File(imageBytes, contentType);
            }
        }

        return NotFound();
    }

    #region Helper Methods

    private IActionResult MergeSearchResults(
        (byte[]? Body, string? ContentType, bool Success) subsonicResult,
        SearchResult externalResult,
        List<ExternalPlaylist> playlistResult,
        string format)
    {
        var (localSongs, localAlbums, localArtists) = subsonicResult.Success && subsonicResult.Body != null
            ? _modelMapper.ParseSearchResponse(subsonicResult.Body, subsonicResult.ContentType)
            : (new List<object>(), new List<object>(), new List<object>());

        var isJson = format == "json" || subsonicResult.ContentType?.Contains("json") == true;
        var (mergedSongs, mergedAlbums, mergedArtists) = _modelMapper.MergeSearchResults(
            localSongs,
            localAlbums,
            localArtists,
            externalResult,
            playlistResult,
            isJson);

        if (isJson)
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                status = "ok",
                version = "1.16.1",
                searchResult3 = new
                {
                    song = mergedSongs,
                    album = mergedAlbums,
                    artist = mergedArtists
                }
            });
        }
        else
        {
            var ns = XNamespace.Get("http://subsonic.org/restapi");
            var searchResult3 = new XElement(ns + "searchResult3");

            foreach (var artist in mergedArtists.Cast<XElement>())
            {
                searchResult3.Add(artist);
            }
            foreach (var album in mergedAlbums.Cast<XElement>())
            {
                searchResult3.Add(album);
            }
            foreach (var song in mergedSongs.Cast<XElement>())
            {
                searchResult3.Add(song);
            }

            var doc = new XDocument(
                new XElement(ns + "subsonic-response",
                    new XAttribute("status", "ok"),
                    new XAttribute("version", "1.16.1"),
                    searchResult3
                )
            );

            return Content(doc.ToString(), "application/xml");
        }
    }

    private string GetContentType(string filePath)
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

    #endregion

    /// <summary>
    /// Stars (favorites) an item. For playlists, this triggers a full download.
    /// </summary>
    [HttpGet, HttpPost]
    [Route("rest/star")]
    [Route("rest/star.view")]
    public async Task<IActionResult> Star()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

        // Check if this is a playlist
        var playlistId = parameters.GetValueOrDefault("id", "");

        if (!string.IsNullOrEmpty(playlistId) && PlaylistIdHelper.IsExternalPlaylist(playlistId))
        {
            if (CurrentProtocolContext.Actor == null)
            {
                return _responseBuilder.CreateError(
                    format,
                    40,
                    "A linked Allstarr user is required for external favorite actions");
            }

            await RecordFavoriteEventSafelyAsync(playlistId, FavoriteOperation.Favorite);

            // Return success response immediately
            return _responseBuilder.CreateResponse(format, "starred", new { });
        }

        // For non-playlist items, relay to real Subsonic server
        try
        {
            var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/star";
            var result = await _proxyService.RelayRawAsync(
                relayEndpoint,
                parameters,
                HttpContext.RequestAborted,
                Request.Headers);
            if ((int)result.StatusCode is >= 200 and < 300 && CurrentProtocolContext.Actor != null)
            {
                foreach (var itemId in parameters.GetAllValues("id").Where(item => !string.IsNullOrWhiteSpace(item)))
                {
                    await RecordFavoriteEventSafelyAsync(itemId, FavoriteOperation.Favorite);
                }
            }
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                "Error connecting to Subsonic server for star operation ({ExceptionType})",
                ex.GetType().Name);
            return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server");
        }
    }

    [HttpGet, HttpPost]
    [Route("rest/unstar")]
    [Route("rest/unstar.view")]
    public async Task<IActionResult> Unstar()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/unstar";
        try
        {
            var result = await _proxyService.RelayRawAsync(
                relayEndpoint, parameters, HttpContext.RequestAborted, Request.Headers);
            if ((int)result.StatusCode is >= 200 and < 300 && CurrentProtocolContext.Actor != null)
            {
                foreach (var itemId in parameters.GetAllValues("id").Where(item => !string.IsNullOrWhiteSpace(item)))
                {
                    await RecordFavoriteEventSafelyAsync(itemId, FavoriteOperation.Unfavorite);
                }
            }
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Error connecting to Subsonic server for unstar operation ({ExceptionType})",
                ex.GetType().Name);
            return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server");
        }
    }

    [HttpGet, HttpPost]
    [Route("rest/updatePlaylist")]
    [Route("rest/updatePlaylist.view")]
    public async Task<IActionResult> UpdatePlaylist()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var playlistId = parameters.GetValueOrDefault("playlistId", "");
        if (!_virtualPlaylistProtocolAdapter.IsVirtualPlaylistId(playlistId))
        {
            return await RelayMutation("rest/updatePlaylist", parameters);
        }

        var route = await _virtualPlaylistProtocolAdapter.ResolveMutationAsync(
            CurrentProtocolContext,
            playlistId,
            HttpContext.RequestAborted);
        if (route == null)
        {
            return _responseBuilder.CreateError(format, 50, "Playlist is not available to this user");
        }

        if (!route.Writable || string.IsNullOrWhiteSpace(route.TargetPlaylistId))
        {
            return _responseBuilder.CreateError(format, 50, "Playlist is read-only");
        }

        return await RelayMutation(
            "rest/updatePlaylist",
            parameters.ReplaceValue("playlistId", route.TargetPlaylistId));
    }

    [HttpGet, HttpPost]
    [Route("rest/scrobble")]
    [Route("rest/scrobble.view")]
    public async Task<IActionResult> Scrobble()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/scrobble";
        try
        {
            var result = await _proxyService.RelayRawAsync(
                relayEndpoint,
                parameters,
                HttpContext.RequestAborted,
                Request.Headers);
            if ((int)result.StatusCode is >= 200 and < 300)
            {
                await RecordScrobbleSignalsSafelyAsync(parameters);
            }
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                "Error connecting to Subsonic server for {Endpoint} ({ExceptionType})",
                relayEndpoint,
                ex.GetType().Name);
            return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server");
        }
    }

    private async Task<IActionResult> RelayMutation(string endpoint)
    {
        var parameters = await ExtractAllParameters();
        return await RelayMutation(endpoint, parameters);
    }

    private async Task<IActionResult> RelayMutation(
        string endpoint,
        SubsonicRequestParameters parameters)
    {
        var format = parameters.GetValueOrDefault("f", "xml");
        var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? endpoint;
        try
        {
            var result = await _proxyService.RelayRawAsync(
                relayEndpoint,
                parameters,
                HttpContext.RequestAborted,
                Request.Headers);
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                "Error connecting to Subsonic server for {Endpoint} ({ExceptionType})",
                relayEndpoint,
                ex.GetType().Name);
            return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server");
        }
    }

    private async Task RecordFavoriteEventSafelyAsync(string itemId, FavoriteOperation operation)
    {
        if (_favoriteActions == null) return;
        try
        {
            var sourceRevision = Request.Headers["Idempotency-Key"].FirstOrDefault()
                ?? Request.Headers["X-Allstarr-Source-Revision"].FirstOrDefault()
                ?? "protocol-state-v1";
            await _favoriteActions.RecordAsync(
                new FavoriteMutationRequest(CurrentProtocolContext, itemId, operation, sourceRevision),
                HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Favorite workflow recording failed ({ExceptionType})", ex.GetType().Name);
        }
    }

    private async Task RecordScrobbleSignalsSafelyAsync(SubsonicRequestParameters parameters)
    {
        if (_playbackSignals == null || CurrentProtocolContext.Actor == null) return;

        foreach (var signal in _scrobbleProtocolAdapter.Parse(parameters, DateTimeOffset.UtcNow))
        {
            try
            {
                await _playbackSignals.RecordAsync(new PlaybackSignalRequest(
                    CurrentProtocolContext,
                    signal.Transition,
                    signal.ItemId,
                    CurrentProtocolContext.Client.DeviceId ?? CurrentProtocolContext.Client.ClientId,
                    $"subsonic:{signal.EventKey}:{signal.Index}",
                    null,
                    signal.ObservedAt), HttpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Could not durably record Subsonic scrobble item {ItemIndex} ({ExceptionType})",
                    signal.Index,
                    ex.GetType().Name);
            }
        }
    }

    // Generic endpoint to handle all subsonic API calls
    [HttpGet, HttpPost]
    [Route("{**endpoint}")]
    public async Task<IActionResult> GenericEndpoint(string endpoint)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

        try
        {
            var result = await _proxyService.RelayRawAsync(
                endpoint,
                parameters,
                HttpContext.RequestAborted,
                Request.Headers);
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
        }
        catch (HttpRequestException ex)
        {
            // Return Subsonic-compatible error response
            _logger.LogError(
                "Error connecting to Subsonic server for endpoint {Endpoint} ({ExceptionType})",
                endpoint,
                ex.GetType().Name);
            return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server");
        }
    }

}
