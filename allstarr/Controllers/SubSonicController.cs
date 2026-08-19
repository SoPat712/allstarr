using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
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
using allstarr.Core.Intelligence;

namespace allstarr.Controllers;

[ApiController]
[Route("")]
[ServiceFilter(typeof(SubsonicAuthFilter), Order = int.MinValue)]
[ServiceFilter(typeof(ProtocolExecutionContextFilter), Order = int.MinValue + 1)]
[ServiceFilter(typeof(SubsonicExceptionFilter))]
public partial class SubsonicController : ControllerBase
{
    private const int MaximumArtworkBytes = 10 * 1024 * 1024;

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
    private readonly IApplicationCache _cache;
    private readonly IMediaAssetResolver _mediaAssets;
    private readonly ILogger<SubsonicController> _logger;
    private readonly IFavoriteActionPipeline? _favoriteActions;
    private readonly IPlaybackSignalPipeline? _playbackSignals;
    private readonly IProtocolProviderGateway? _providerGateway;
    private readonly ProtocolStreamingResponseAdapter? _streamingResponseAdapter;
    private readonly IAudioMuseRecommendationClient? _audioMuse;
    private readonly IProtocolLibraryScopeResolver? _libraryScopes;
    private readonly IIntelligencePolicyService? _intelligencePolicies;
    private readonly ManagedTrackCacheService? _managedTrackCache;

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
        IApplicationCache cache,
        IMediaAssetResolver mediaAssets,
        ILogger<SubsonicController> logger,
        IFavoriteActionPipeline? favoriteActions = null,
        IPlaybackSignalPipeline? playbackSignals = null,
        IProtocolProviderGateway? providerGateway = null,
        ProtocolStreamingResponseAdapter? streamingResponseAdapter = null,
        IAudioMuseRecommendationClient? audioMuse = null,
        IProtocolLibraryScopeResolver? libraryScopes = null,
        IIntelligencePolicyService? intelligencePolicies = null,
        ManagedTrackCacheService? managedTrackCache = null)
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
        _cache = cache;
        _mediaAssets = mediaAssets;
        _logger = logger;
        _favoriteActions = favoriteActions;
        _playbackSignals = playbackSignals;
        _providerGateway = providerGateway;
        _streamingResponseAdapter = streamingResponseAdapter;
        _audioMuse = audioMuse;
        _libraryScopes = libraryScopes;
        _intelligencePolicies = intelligencePolicies;
        _managedTrackCache = managedTrackCache;

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
            HttpContext.RequireProtocolExecutionContext(),
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

    private Task<List<Album>> GetProviderArtistAlbumsAsync(string provider, string externalId) =>
        _providerGateway != null
            ? _providerGateway.GetArtistAlbumsAsync(CurrentProtocolContext, provider, externalId)
            : _metadataService.GetArtistAlbumsAsync(provider, externalId, HttpContext.RequestAborted);

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
            var result = await _proxyService.RelayRawAsync(
                "rest/search3",
                parameters,
                HttpContext.RequestAborted);
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
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
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            var result = _responseBuilder.CreateError(format, 10, "Missing id parameter");
            if (result is JsonResult json) json.StatusCode = StatusCodes.Status400BadRequest;
            if (result is ContentResult content) content.StatusCode = StatusCodes.Status400BadRequest;
            return result;
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(id);

        if (!isExternal)
        {
            return await _proxyService.RelayStreamAsync(parameters, HttpContext.RequestAborted);
        }

        var requestedQuality = StreamQualityHelper.FromSubsonicMaxBitRate(
            parameters.GetValueOrDefault("maxBitRate"));
        var localPath = requestedQuality == ProviderAudioQuality.Any
            ? await _localLibraryService.GetLocalPathForExternalSongAsync(provider!, externalId!)
            : null;

        if (localPath != null && System.IO.File.Exists(localPath))
        {
            // Update last write time for cache cleanup (extends cache lifetime)
            try
            {
                System.IO.File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to refresh cached Subsonic stream age ({ExceptionType})",
                    ex.GetType().Name);
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
                    requestedQuality,
                    Request.Headers.Range.ToString() is { Length: > 0 } range ? range : null,
                    headOnly: HttpMethods.IsHead(Request.Method));
                if (routed != null)
                {
                    if (!routed.Response.IsSuccessStatusCode)
                    {
                        var status = (int)routed.Response.StatusCode;
                        routed.Response.Dispose();
                        return StatusCode(status);
                    }
                    if (_managedTrackCache != null)
                    {
                        await _managedTrackCache.WrapAsync(
                            routed,
                            provider!,
                            externalId!,
                            requestedQuality,
                            HttpMethods.IsHead(Request.Method),
                            () => _providerGateway.GetSongAsync(CurrentProtocolContext, provider!, externalId!),
                            HttpContext.RequestAborted);
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
                var error = SubsonicExceptionFilter.Map(
                    ex,
                    HttpContext.RequestAborted.IsCancellationRequested);
                _logger.LogWarning(
                    "Typed provider stream route failed safely for {Provider} ({ExceptionType})",
                    provider,
                    ex.GetType().Name);
                return StatusCode(error.StatusCode, new { error = error.Message });
            }
        }

        try
        {
            var downloadStream = await _downloadService.DownloadAndStreamAsync(
                provider!,
                externalId!,
                requestedQuality switch
                {
                    ProviderAudioQuality.DataSaver => StreamQuality.Low,
                    ProviderAudioQuality.Lossy => StreamQuality.High,
                    _ => null
                },
                HttpContext.RequestAborted);

            var contentType = "audio/mpeg";
            if (downloadStream is FileStream fs)
            {
                contentType = GetContentType(fs.Name);
            }

            return File(downloadStream, contentType, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            var error = SubsonicExceptionFilter.Map(
                ex,
                HttpContext.RequestAborted.IsCancellationRequested);
            _logger.LogError(
                "Failed to stream external Subsonic item {Id} safely ({ExceptionType})",
                id,
                ex.GetType().Name);
            return StatusCode(error.StatusCode, new { error = error.Message });
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
    /// Returns provider-backed artists and relays native artists unchanged.
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

            var albums = await GetProviderArtistAlbumsAsync(provider!, externalId!);

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

        var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getArtist";
        var nativeResult = await _proxyService.RelayRawAsync(
            relayEndpoint,
            parameters,
            HttpContext.RequestAborted);
        return _relayProtocolAdapter.CreateResult(nativeResult, $"application/{format}");
    }

    /// <summary>
    /// Returns provider-backed albums and relays native albums unchanged.
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

            // Convert to album response (playlist as album)
            return _responseBuilder.CreatePlaylistAsAlbumResponse(format, playlist, tracks);
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

        var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getAlbum";
        var nativeResult = await _proxyService.RelayRawAsync(
            relayEndpoint,
            parameters,
            HttpContext.RequestAborted);
        return _relayProtocolAdapter.CreateResult(nativeResult, $"application/{format}");
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

    [HttpGet, HttpPost]
    [Route("rest/getPlaylists")]
    [Route("rest/getPlaylists.view")]
    public async Task<IActionResult> GetPlaylists()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var endpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getPlaylists";
        var result = await _proxyService.RelayRawAsync(
            endpoint, parameters, HttpContext.RequestAborted, Request.Headers);
        var merged = await _virtualPlaylistProtocolAdapter.ListAsync(
            CurrentProtocolContext,
            format,
            result,
            HttpContext.RequestAborted);
        return _relayProtocolAdapter.CreateResult(merged, $"application/{format}");
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
        var format = parameters.GetValueOrDefault("f", "xml");

        if (string.IsNullOrWhiteSpace(id))
        {
            var result = _responseBuilder.CreateError(format, 10, "Missing id parameter");
            if (result is JsonResult json) json.StatusCode = StatusCodes.Status400BadRequest;
            if (result is ContentResult content) content.StatusCode = StatusCodes.Status400BadRequest;
            return result;
        }

        // Check if this is a playlist cover art request
        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            try
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);
                var playlist = _providerGateway != null
                    ? await _providerGateway.GetPlaylistAsync(CurrentProtocolContext, provider, externalId)
                    : await _metadataService.GetPlaylistAsync(provider, externalId);

                if (playlist == null || string.IsNullOrEmpty(playlist.CoverUrl))
                {
                    return NotFound();
                }

                var asset = await ResolveExternalImageAsync(
                    provider, "playlist", externalId, playlist.CoverUrl);
                return asset == null ? NotFound() : File(asset.Bytes, asset.ContentType);
            }
            catch (Exception ex)
            {
                var error = SubsonicExceptionFilter.Map(
                    ex,
                    HttpContext.RequestAborted.IsCancellationRequested);
                _logger.LogWarning(
                    "Playlist cover art failed safely ({ExceptionType})",
                    ex.GetType().Name);
                return StatusCode(error.StatusCode);
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
            catch (Exception ex)
            {
                var error = SubsonicExceptionFilter.Map(
                    ex,
                    HttpContext.RequestAborted.IsCancellationRequested);
                _logger.LogWarning(
                    "Native Subsonic cover art relay failed safely ({ExceptionType})",
                    ex.GetType().Name);
                return StatusCode(error.StatusCode);
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
            var asset = await ResolveExternalImageAsync(
                coverProvider!, type ?? "song", coverExternalId!, coverUrl);
            if (asset != null)
                return File(asset.Bytes, asset.ContentType);
        }

        return NotFound();
    }

    private async Task<ResolvedMediaAsset?> ResolveExternalImageAsync(
        string provider,
        string resourceKind,
        string resourceId,
        string coverUrl)
    {
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                coverUrl, out var coverUri, out var validationReason) || coverUri == null)
        {
            _logger.LogWarning(
                "Blocked external image URL for {Provider}/{ResourceId}: {Reason}",
                provider, resourceId, validationReason);
            return null;
        }

        var actor = CurrentProtocolContext.Actor;
        return await _mediaAssets.ResolveAsync(
            new MediaAssetIdentity(
                actor?.TenantId,
                actor?.EffectiveUserId,
                null,
                provider,
                resourceKind,
                resourceId),
            async token =>
            {
                using var response = await _proxyService.HttpClient.GetAsync(coverUri, token);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength > MaximumArtworkBytes ||
                    contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == false)
                    return null;
                await response.Content.LoadIntoBufferAsync(MaximumArtworkBytes, token);
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                return new MediaAssetSource(
                    bytes,
                    contentType ?? "image/jpeg",
                    response.Headers.ETag?.Tag,
                    response.Content.Headers.LastModified);
            },
            MaximumArtworkBytes,
            HttpContext.RequestAborted);
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
                    50,
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
