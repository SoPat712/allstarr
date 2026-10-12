using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
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
using allstarr.Core.Matching;
using allstarr.Core.Music;

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
    private readonly ILocalLibraryService _localLibraryService;
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
    private readonly MusicFavorites _favorites;
    private readonly MusicPlaybackObservations _playbackObservations;
    private readonly IProtocolProviderGateway _providerGateway;
    private readonly ProtocolStreamingResponseAdapter _streamingResponseAdapter;
    private readonly MusicSearchService _musicSearch;
    private readonly ProviderStreamOpener _streamOpener;
    private readonly MusicMixService _musicMix;
    private readonly MusicCatalogMerge _catalogMerge;
    private readonly IAudioMuseRecommendationClient? _audioMuse;
    private readonly IIntelligencePolicyService? _intelligencePolicies;
    private readonly ILibraryIndexService? _libraryIndex;

    public SubsonicController(
        IOptions<SubsonicSettings> subsonicSettings,
        ILocalLibraryService localLibraryService,
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
        IProtocolProviderGateway providerGateway,
        ProtocolStreamingResponseAdapter streamingResponseAdapter,
        MusicSearchService musicSearch,
        ProviderStreamOpener streamOpener,
        MusicMixService musicMix,
        MusicCatalogMerge catalogMerge,
        IFavoriteActionPipeline? favoriteActions = null,
        IPlaybackSignalPipeline? playbackSignals = null,
        IAudioMuseRecommendationClient? audioMuse = null,
        IIntelligencePolicyService? intelligencePolicies = null,
        ILibraryIndexService? libraryIndex = null)
    {
        _subsonicSettings = subsonicSettings.Value;
        _libraryIndex = libraryIndex;
        _localLibraryService = localLibraryService;
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
        _favorites = new MusicFavorites(favoriteActions, logger);
        _playbackObservations = new MusicPlaybackObservations(playbackSignals, logger);
        _providerGateway = providerGateway;
        _streamingResponseAdapter = streamingResponseAdapter;
        _musicSearch = musicSearch;
        _streamOpener = streamOpener;
        _musicMix = musicMix;
        _catalogMerge = catalogMerge;
        _audioMuse = audioMuse;
        _intelligencePolicies = intelligencePolicies;

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

    // Reuse the authenticated query and form projection when available.
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

    private Task<Song?> GetProviderSongAsync(string provider, string externalId) =>
        _providerGateway.GetSongAsync(CurrentProtocolContext, provider, externalId);

    private Task<Album?> GetProviderAlbumAsync(string provider, string externalId) =>
        _providerGateway.GetAlbumAsync(CurrentProtocolContext, provider, externalId);

    private Task<Artist?> GetProviderArtistAsync(string provider, string externalId) =>
        _providerGateway.GetArtistAsync(CurrentProtocolContext, provider, externalId);

    private Task<List<Album>> GetProviderArtistAlbumsAsync(string provider, string externalId) =>
        _providerGateway.GetArtistAlbumsAsync(CurrentProtocolContext, provider, externalId);

    [HttpGet, HttpPost]
    [Route("rest/search2")]
    [Route("rest/search2.view")]
    public Task<IActionResult> Search2() => SearchAsync(SubsonicSearchProtocolAdapter.Search2);

    [HttpGet, HttpPost]
    [Route("rest/search3")]
    [Route("rest/search3.view")]
    public Task<IActionResult> Search3() => SearchAsync(SubsonicSearchProtocolAdapter.Search3);

    private async Task<IActionResult> SearchAsync(string method)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var window = _searchProtocolAdapter.Parse(parameters, CurrentProtocolContext);
        var cleanQuery = window.Query;

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            var result = await _proxyService.RelayRawAsync(
                $"rest/{method}",
                parameters,
                HttpContext.RequestAborted);
            return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
        }

        var native = new SubsonicNativeMusicSearch(_proxyService, _modelMapper, _responseBuilder, parameters, format, method);
        var playlists = _subsonicSettings.EnableExternalPlaylists
            ? new MusicSearchWindow(window.AlbumFetchCount, window.AlbumOffset, window.AlbumCount)
            : MusicSearchWindow.None;
        var merged = await _musicSearch.SearchAsync(
            CurrentProtocolContext,
            new MusicSearchRequest(
                cleanQuery,
                new MusicSearchWindow(window.SongFetchCount, window.SongOffset, window.SongCount),
                new MusicSearchWindow(window.AlbumFetchCount, window.AlbumOffset, window.AlbumCount),
                new MusicSearchWindow(window.ArtistFetchCount, window.ArtistOffset, window.ArtistCount),
                playlists,
                HideProviderArtistsNamedLikeNative: true),
            native,
            HttpContext.RequestAborted);
        var rendered = _modelMapper.RenderSearchResult(
            merged,
            native.IsJson,
            SubsonicSearchProtocolAdapter.UsesDirectoryAlbums(method));
        return RenderSearchResult(rendered, native.IsJson, SubsonicSearchProtocolAdapter.ResultElement(method));
    }

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
        if (isExternal && _libraryIndex != null &&
            await _libraryIndex.ResolveKnownTrackAsync(CurrentProtocolContext, id, HttpContext.RequestAborted) is { } nativeId)
        {
            id = nativeId;
            parameters = parameters.SetValue("id", nativeId);
            isExternal = false;
        }

        if (!isExternal)
        {
            return await _proxyService.RelayStreamAsync(parameters, HttpContext.RequestAborted);
        }

        var requestedQuality = StreamQualityHelper.FromSubsonicMaxBitRate(
            parameters.GetValueOrDefault("maxBitRate"));
        try
        {
            return await _streamOpener.OpenAsync(
                HttpContext,
                CurrentProtocolContext with { RequestedItemId = id },
                provider!,
                externalId!,
                requestedQuality);
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

    // Folder-based clients open provider albums, artists and playlists found through search2 here.
    [HttpGet, HttpPost]
    [Route("rest/getMusicDirectory")]
    [Route("rest/getMusicDirectory.view")]
    public async Task<IActionResult> GetMusicDirectory()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var id = parameters.GetValueOrDefault("id", "");
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        string? name = null;
        var children = new List<object>();

        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);
            var playlist = await _providerGateway.GetPlaylistAsync(CurrentProtocolContext, provider, externalId);
            if (playlist != null)
            {
                name = playlist.Name;
                var tracks = await _providerGateway.GetPlaylistTracksAsync(CurrentProtocolContext, provider, externalId);
                children.AddRange(tracks.Select(song => SongChild(song)));
            }
        }
        else
        {
            var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(id);
            if (!isExternal || type is not ("album" or "artist"))
            {
                var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getMusicDirectory";
                var relayed = await _proxyService.RelayRawAsync(relayEndpoint, parameters, HttpContext.RequestAborted, Request.Headers);
                return _relayProtocolAdapter.CreateResult(relayed, $"application/{format}");
            }
            if (type == "album")
            {
                var album = await GetProviderAlbumAsync(provider!, externalId!);
                if (album != null)
                {
                    name = album.Title;
                    children.AddRange(album.Songs.Select(song => SongChild(song)));
                }
            }
            else
            {
                var artist = await GetProviderArtistAsync(provider!, externalId!);
                if (artist != null)
                {
                    name = artist.Name;
                    foreach (var album in await GetProviderArtistAlbumsAsync(provider!, externalId!))
                    {
                        album.Artist ??= artist.Name;
                        album.ArtistId ??= artist.Id;
                        children.Add(format == "json"
                            ? _responseBuilder.ConvertAlbumToDirectoryJson(album)
                            : _responseBuilder.ConvertAlbumToDirectoryXml(album, ns));
                    }
                }
            }
        }

        if (name == null) return _responseBuilder.CreateError(format, 70, "Directory not found");
        if (format == "json")
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                status = "ok",
                version = "1.16.1",
                directory = new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["child"] = children }
            });
        }
        var directory = new XElement(ns + "directory", new XAttribute("id", id), new XAttribute("name", name));
        foreach (var child in children.OfType<XElement>())
        {
            child.Name = ns + "child";
            directory.Add(child);
        }
        return Content(new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"),
            new XAttribute("version", "1.16.1"),
            directory)).ToString(), "application/xml");

        object SongChild(Song song) => format == "json"
            ? _responseBuilder.ConvertSongToJson(song)
            : _responseBuilder.ConvertSongToXml(song, ns);
    }

    // Provider seeds get a mix from the shared mix owner; native seeds keep the backend's answer.
    [HttpGet, HttpPost]
    [Route("rest/getSimilarSongs")]
    [Route("rest/getSimilarSongs.view")]
    public Task<IActionResult> GetSimilarSongs() => GetProviderMixAsync("similarSongs", artistOnly: false);

    [HttpGet, HttpPost]
    [Route("rest/getSimilarSongs2")]
    [Route("rest/getSimilarSongs2.view")]
    public Task<IActionResult> GetSimilarSongs2() => GetProviderMixAsync("similarSongs2", artistOnly: true);

    private async Task<IActionResult> GetProviderMixAsync(string element, bool artistOnly)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var id = parameters.GetValueOrDefault("id", "");
        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(id);
        if (!isExternal || (artistOnly && type != "artist") || type is not ("song" or "album" or "artist"))
        {
            var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? $"rest/get{char.ToUpperInvariant(element[0])}{element[1..]}";
            var relayed = await _proxyService.RelayRawAsync(relayEndpoint, parameters, HttpContext.RequestAborted, Request.Headers);
            return _relayProtocolAdapter.CreateResult(relayed, $"application/{format}");
        }

        var count = int.TryParse(parameters.GetValueOrDefault("count"), out var requested) && requested > 0
            ? Math.Min(requested, 500)
            : 50;
        IReadOnlyList<Song> songs;
        try
        {
            songs = await _musicMix.InstantMixAsync(CurrentProtocolContext, provider!, type!, externalId!, id, count);
        }
        catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Provider mix for {Provider} was unavailable ({ExceptionType})", provider, error.GetType().Name);
            songs = [];
        }
        return CreateSongListResponse(format, element, songs);
    }

    // Provider artists have no backend record; answer with the provider's similar artists, or an empty,
    // valid artist info when the provider has none.
    [HttpGet, HttpPost]
    [Route("rest/getArtistInfo")]
    [Route("rest/getArtistInfo.view")]
    public Task<IActionResult> GetArtistInfo() => GetArtistInfoAsync("artistInfo");

    [HttpGet, HttpPost]
    [Route("rest/getArtistInfo2")]
    [Route("rest/getArtistInfo2.view")]
    public Task<IActionResult> GetArtistInfo2() => GetArtistInfoAsync("artistInfo2");

    private async Task<IActionResult> GetArtistInfoAsync(string element)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var (isExternal, provider, type, externalId) = _localLibraryService.ParseExternalId(parameters.GetValueOrDefault("id", ""));
        if (!isExternal || type != "artist")
        {
            var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? $"rest/get{char.ToUpperInvariant(element[0])}{element[1..]}";
            var relayed = await _proxyService.RelayRawAsync(relayEndpoint, parameters, HttpContext.RequestAborted, Request.Headers);
            return _relayProtocolAdapter.CreateResult(relayed, $"application/{format}");
        }

        var count = int.TryParse(parameters.GetValueOrDefault("count"), out var requested) && requested > 0
            ? Math.Min(requested, 200)
            : 20;
        IReadOnlyList<Artist> similarArtists;
        try
        {
            similarArtists = (await _providerGateway.GetSimilarArtistsAsync(CurrentProtocolContext, provider!, externalId!, count))
                .Take(count).ToList();
        }
        catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Similar artists for {Provider} were unavailable ({ExceptionType})", provider, error.GetType().Name);
            similarArtists = [];
        }
        return CreateSongListResponse(format, element, [], songsElement: null, similarArtists);
    }

    private IActionResult CreateSongListResponse(string format, string element, IReadOnlyList<Song> songs,
        string? songsElement = "song", IReadOnlyList<Artist>? similarArtists = null)
    {
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        if (format == "json")
        {
            var body = new Dictionary<string, object>();
            if (songsElement != null) body[songsElement] = songs.Select(song => _responseBuilder.ConvertSongToJson(song)).ToList();
            if (similarArtists is { Count: > 0 })
                body["similarArtist"] = similarArtists.Select(_responseBuilder.ConvertArtistToJson).ToList();
            return _responseBuilder.CreateJsonResponse(new Dictionary<string, object>
            {
                ["status"] = "ok",
                ["version"] = "1.16.1",
                [element] = body
            });
        }
        var list = new XElement(ns + element);
        if (songsElement != null)
            foreach (var song in songs) list.Add(_responseBuilder.ConvertSongToXml(song, ns));
        foreach (var artist in similarArtists ?? [])
        {
            var similar = _responseBuilder.ConvertArtistToXml(artist, ns);
            similar.Name = ns + "similarArtist";
            list.Add(similar);
        }
        return Content(new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"),
            new XAttribute("version", "1.16.1"),
            list)).ToString(), "application/xml");
    }

    // Library top songs come first; provider songs led by the same artist fill the remaining room.
    [HttpGet, HttpPost]
    [Route("rest/getTopSongs")]
    [Route("rest/getTopSongs.view")]
    public async Task<IActionResult> GetTopSongs()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var artist = parameters.GetValueOrDefault("artist", "").Trim();
        var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getTopSongs";
        var nativeTask = _proxyService.RelayRawAsync(relayEndpoint, parameters, HttpContext.RequestAborted, Request.Headers);
        if (artist.Length == 0)
            return _relayProtocolAdapter.CreateResult(await nativeTask, $"application/{format}");

        var count = int.TryParse(parameters.GetValueOrDefault("count"), out var requested) && requested > 0
            ? Math.Min(requested, 500)
            : 50;
        var providerTask = SearchArtistSongsAsync(artist, Math.Min(Math.Max(count, 20) * 2, 100));
        var native = await nativeTask;
        var providerSongs = await providerTask;
        var nativeSongs = native.IsSuccessStatusCode ? _modelMapper.ParseTopSongs(native.Body, native.ContentType) : null;
        var added = nativeSongs == null
            ? []
            : MusicTopSongs.Extend(artist, nativeSongs.Select(SubsonicModelMapper.SongTitle), providerSongs,
                count - nativeSongs.Count);
        if (nativeSongs == null || added.Count == 0)
            return _relayProtocolAdapter.CreateResult(native, $"application/{format}");

        var isJson = native.ContentType?.Contains("json") == true;
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var songs = nativeSongs.Concat(added.Select(song => isJson
            ? (object)_responseBuilder.ConvertSongToJson(song)
            : _responseBuilder.ConvertSongToXml(song, ns))).ToList();
        if (isJson)
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                status = "ok",
                version = "1.16.1",
                topSongs = new { song = songs }
            });
        }
        foreach (var element in songs.OfType<XElement>()) element.Name = ns + "song";
        return Content(new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"),
            new XAttribute("version", "1.16.1"),
            new XElement(ns + "topSongs", songs))).ToString(), "application/xml");
    }

    // A provider outage must not cost the listener the library's own top songs.
    private async Task<IReadOnlyList<Song>> SearchArtistSongsAsync(string artist, int limit)
    {
        try
        {
            return (await _providerGateway.SearchAsync(CurrentProtocolContext, artist, limit, 0, 0)).Songs;
        }
        catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Provider songs for top songs were unavailable ({ExceptionType})", error.GetType().Name);
            return [];
        }
    }

    // OpenSubsonic clients such as Feishin ask before streaming. Provider songs always play
    // directly; native songs, including owned copies of provider songs, keep the backend's answer.
    [HttpGet, HttpPost]
    [Route("rest/getTranscodeDecision")]
    [Route("rest/getTranscodeDecision.view")]
    public async Task<IActionResult> GetTranscodeDecision()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var mediaId = parameters.GetValueOrDefault("mediaId", "");
        var (isExternal, _, _) = _localLibraryService.ParseSongId(mediaId);
        if (isExternal && _libraryIndex != null &&
            await _libraryIndex.ResolveKnownTrackAsync(CurrentProtocolContext, mediaId, HttpContext.RequestAborted) is { } nativeId)
        {
            parameters = parameters.SetValue("mediaId", nativeId);
            isExternal = false;
        }

        if (isExternal)
        {
            return _responseBuilder.CreateDirectPlayDecisionResponse(format);
        }

        var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/getTranscodeDecision";
        var result = await _proxyService.RelayRawAsync(
            relayEndpoint,
            parameters,
            HttpContext.RequestAborted,
            Request.Headers);
        return _relayProtocolAdapter.CreateResult(result, $"application/{format}");
    }

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
        if (isExternal && _libraryIndex != null &&
            await _libraryIndex.ResolveKnownTrackAsync(CurrentProtocolContext, id, HttpContext.RequestAborted) is { } nativeId)
        {
            id = nativeId;
            parameters = parameters.SetValue("id", nativeId);
            isExternal = false;
        }

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

            // Deezer's artist-albums payload omits artist identity.
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
        return await MergeProviderChildrenAsync(nativeResult, format, "artist", "album", async (name, _, titles) =>
            (await _catalogMerge.MissingArtistAlbumsAsync(CurrentProtocolContext, name, titles))
            .Select(album =>
            {
                album.Artist ??= name;
                album.ArtistId = id;
                return format == "json"
                    ? (object)_responseBuilder.ConvertAlbumToJson(album)
                    : _responseBuilder.ConvertAlbumToXml(album, XNamespace.Get("http://subsonic.org/restapi"));
            }).ToList());
    }

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

        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);

            var playlist = await _providerGateway.GetPlaylistAsync(CurrentProtocolContext, provider, externalId);
            if (playlist == null)
            {
                return _responseBuilder.CreateError(format, 70, "Playlist not found");
            }

            var tracks = await _providerGateway.GetPlaylistTracksAsync(CurrentProtocolContext, provider, externalId);

            // Subsonic clients consume external playlists through the album shape.
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
        return await MergeProviderChildrenAsync(nativeResult, format, "album", "song", async (name, artist, titles) =>
            (await _catalogMerge.MissingAlbumSongsAsync(CurrentProtocolContext, name, artist ?? "", titles))
            .Select(song => format == "json"
                ? (object)_responseBuilder.ConvertSongToJson(song)
                : _responseBuilder.ConvertSongToXml(song, XNamespace.Get("http://subsonic.org/restapi")))
            .ToList());
    }

    /// <summary>
    /// Appends provider children (albums of an artist, songs of an album) to a native answer.
    /// Native fields stay untouched; when providers add nothing or fail, the answer passes through.
    /// </summary>
    private async Task<IActionResult> MergeProviderChildrenAsync(
        SubsonicProxyResponse native,
        string format,
        string parentElement,
        string childElement,
        Func<string, string?, IReadOnlyCollection<string>, Task<List<object>>> findMissing)
    {
        var passthrough = _relayProtocolAdapter.CreateResult(native, $"application/{format}");
        if (!native.IsSuccessStatusCode || native.Body.Length == 0) return passthrough;
        var isJson = native.ContentType?.Contains("json") == true;
        if (isJson != (format == "json")) return passthrough;
        var childTitle = childElement == "song" ? "title" : "name";
        try
        {
            if (isJson)
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(native.Body)?["subsonic-response"]?.AsObject();
                if (root?["status"]?.GetValue<string>() != "ok" || root[parentElement] is not System.Text.Json.Nodes.JsonObject parent)
                    return passthrough;
                var name = parent["name"]?.GetValue<string>() ?? "";
                var artist = parent["artist"]?.GetValue<string>();
                var children = parent[childElement] as System.Text.Json.Nodes.JsonArray ?? [];
                var titles = children.Select(child => child?[childTitle]?.GetValue<string>() ?? "").ToList();
                var missing = await FindMissingSafelyAsync(findMissing, name, artist, titles);
                if (missing.Count == 0) return passthrough;
                foreach (var item in missing)
                    children.Add(System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(item)));
                parent[childElement] = children;
                UpdateCounts(parent, childElement, children.Count);
                return Content(new System.Text.Json.Nodes.JsonObject { ["subsonic-response"] = root.DeepClone() }.ToJsonString(),
                    native.ContentType ?? "application/json");
            }

            var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(native.Body));
            var ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var parentXml = document.Root?.Element(ns + parentElement);
            if (document.Root?.Attribute("status")?.Value != "ok" || parentXml == null) return passthrough;
            var xmlTitles = parentXml.Elements(ns + childElement).Select(child => child.Attribute(childTitle)?.Value ?? "").ToList();
            var xmlMissing = await FindMissingSafelyAsync(findMissing, parentXml.Attribute("name")?.Value ?? "",
                parentXml.Attribute("artist")?.Value, xmlTitles);
            if (xmlMissing.Count == 0) return passthrough;
            foreach (var element in xmlMissing.OfType<XElement>())
            {
                element.Name = ns + childElement;
                parentXml.Add(element);
            }
            var total = parentXml.Elements(ns + childElement).Count();
            if (childElement == "album") parentXml.SetAttributeValue("albumCount", total);
            else parentXml.SetAttributeValue("songCount", total);
            return Content(document.ToString(), native.ContentType ?? "application/xml");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or System.Xml.XmlException or InvalidOperationException)
        {
            return passthrough;
        }

        static void UpdateCounts(System.Text.Json.Nodes.JsonObject parent, string child, int total)
        {
            if (child == "album") parent["albumCount"] = total;
            else parent["songCount"] = total;
        }
    }

    private async Task<List<object>> FindMissingSafelyAsync(
        Func<string, string?, IReadOnlyCollection<string>, Task<List<object>>> findMissing,
        string name,
        string? artist,
        IReadOnlyCollection<string> titles)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        try
        {
            return await findMissing(name, artist, titles);
        }
        catch (Exception error) when (error is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Provider additions were unavailable ({ExceptionType})", error.GetType().Name);
            return [];
        }
    }

    // Virtual playlists are read-only projections; native IDs remain transparent relays.
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

    // Synthetic cover IDs encode provider and type as ext-{provider}-{type}-{id}.
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

        if (PlaylistIdHelper.IsExternalPlaylist(id))
        {
            try
            {
                var (provider, externalId) = PlaylistIdHelper.ParsePlaylistId(id);
                var playlist = await _providerGateway.GetPlaylistAsync(CurrentProtocolContext, provider, externalId);

                var asset = await ResolveExternalImageAsync(
                    provider, "playlist", externalId, playlist?.CoverUrl);
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
                var song = await GetProviderSongAsync(coverProvider!, coverExternalId!);
                if (song?.CoverArtUrl != null)
                {
                    coverUrl = song.CoverArtUrl;
                }
                else
                {
                    // Legacy cover IDs may refer to an album with the same external ID.
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
        string? coverUrl)
    {
        var actor = CurrentProtocolContext.Actor;
        return await _mediaAssets.ResolveAsync(
            new MediaAssetIdentity(
                actor?.EffectiveUserId,
                null,
                provider,
                resourceKind,
                resourceId),
            async token =>
            {
                if (resourceKind == "playlist")
                {
                    var artwork = await _providerGateway.ResolvePlaylistArtworkAsync(
                        CurrentProtocolContext, provider, resourceId, MaximumArtworkBytes);
                    if (artwork != null)
                        return new MediaAssetSource(artwork.Bytes, artwork.ContentType);
                }

                if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                        coverUrl, out var coverUri, out var validationReason) || coverUri == null)
                {
                    _logger.LogWarning(
                        "Blocked external image URL for {Provider}/{ResourceId}: {Reason}",
                        provider, resourceId, validationReason);
                    return null;
                }

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

    private IActionResult RenderSearchResult(
        (List<object> Songs, List<object> Albums, List<object> Artists) merged,
        bool isJson,
        string resultElement)
    {
        var (mergedSongs, mergedAlbums, mergedArtists) = merged;
        if (isJson)
        {
            return _responseBuilder.CreateJsonResponse(new Dictionary<string, object>
            {
                ["status"] = "ok",
                ["version"] = "1.16.1",
                [resultElement] = new
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
            var searchResult = new XElement(ns + resultElement);

            foreach (var artist in mergedArtists.Cast<XElement>())
            {
                searchResult.Add(artist);
            }
            foreach (var album in mergedAlbums.Cast<XElement>())
            {
                searchResult.Add(album);
            }
            foreach (var song in mergedSongs.Cast<XElement>())
            {
                searchResult.Add(song);
            }

            var doc = new XDocument(
                new XElement(ns + "subsonic-response",
                    new XAttribute("status", "ok"),
                    new XAttribute("version", "1.16.1"),
                    searchResult
                )
            );

            return Content(doc.ToString(), "application/xml");
        }
    }

    #endregion

    [HttpGet, HttpPost]
    [Route("rest/star")]
    [Route("rest/star.view")]
    public async Task<IActionResult> Star()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");

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

            return _responseBuilder.CreateResponse(format, "starred", new { });
        }

        try
        {
            var relayEndpoint = Request.Path.Value?.TrimStart('/') ?? "rest/star";
            var result = await _proxyService.RelayRawAsync(
                relayEndpoint,
                parameters,
                HttpContext.RequestAborted,
                Request.Headers);
            if ((int)result.StatusCode is >= 200 and < 300)
            {
                await RecordFavoriteEventsAsync(parameters, FavoriteOperation.Favorite);
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
            if ((int)result.StatusCode is >= 200 and < 300)
            {
                await RecordFavoriteEventsAsync(parameters, FavoriteOperation.Unfavorite);
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

    private Task RecordFavoriteEventSafelyAsync(string itemId, FavoriteOperation operation) =>
        _favorites.RecordAsync(CurrentProtocolContext, itemId, operation, Request.Headers, HttpContext.RequestAborted);

    private async Task RecordFavoriteEventsAsync(SubsonicRequestParameters parameters, FavoriteOperation operation)
    {
        foreach (var itemId in parameters.GetAllValues("id").Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            await RecordFavoriteEventSafelyAsync(itemId, operation);
        }
    }

    private async Task RecordScrobbleSignalsSafelyAsync(SubsonicRequestParameters parameters)
    {
        var context = CurrentProtocolContext;
        foreach (var signal in _scrobbleProtocolAdapter.Parse(parameters, DateTimeOffset.UtcNow))
        {
            if (!await _playbackObservations.RecordAsync(
                    _scrobbleProtocolAdapter.ToSignalRequest(context, signal),
                    HttpContext.RequestAborted))
            {
                return;
            }
        }
    }

    // Lowest-priority catch-all relays unhandled Subsonic methods unchanged.
    [HttpGet, HttpPost]
    [Route("{**endpoint}")]
    public async Task<IActionResult> GenericEndpoint(string endpoint)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        if (!SubsonicMusicEndpointPolicy.CanRelay(endpoint))
        {
            // Allstarr only exposes music; video, podcasts, sharing and administration stay behind it.
            var denied = _responseBuilder.CreateError(format, 70, "This method is not available through Allstarr");
            switch (denied)
            {
                case JsonResult json: json.StatusCode = StatusCodes.Status404NotFound; break;
                case ContentResult content: content.StatusCode = StatusCodes.Status404NotFound; break;
            }
            return denied;
        }

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
            _logger.LogError(
                "Error connecting to Subsonic server for endpoint {Endpoint} ({ExceptionType})",
                endpoint,
                ex.GetType().Name);
            return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server");
        }
    }

}
