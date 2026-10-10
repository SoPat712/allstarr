using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Services;
using System.Net;

namespace allstarr.Core.Providers.Qobuz;

public sealed class QobuzProvider : ProviderCatalogMetadata
{
    public const string StableProviderId = "qobuz";
    private const string BaseUrl = "https://www.qobuz.com/api.json/0.2/";
    private const int PageSize = 500;
    private const int MaximumPages = 200;
    private readonly HttpClient _httpClient;
    private readonly QobuzBundleService _bundleService;
    private readonly IProviderAccountSecretAccessor _secrets;
    private readonly ILogger<QobuzProvider> _logger;
    private readonly string? _userAuthToken;

    public QobuzProvider(HttpClient http, QobuzBundleService bundles,
        IProviderAccountSecretAccessor secrets, ILogger<QobuzProvider> logger)
        : this(http, bundles, secrets, logger, null) { }

    private QobuzProvider(HttpClient http, QobuzBundleService bundles,
        IProviderAccountSecretAccessor secrets, ILogger<QobuzProvider> logger, string? token)
        : base(StableProviderId)
    {
        _httpClient = http;
        _bundleService = bundles;
        _secrets = secrets;
        _logger = logger;
        _userAuthToken = token;
    }

    internal override Task<T> UseCatalogAsync<T>(
        ProviderExecutionContext context, Func<ProviderCatalogMetadata, Task<T>> operation)
    {
        if (context.Account == null) return operation(this);
        return _secrets.UseAsync(context.Account, bytes =>
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("userAuthToken", out var token) ||
                token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()) ||
                !root.TryGetProperty("userId", out var user) ||
                user.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(user.GetString()))
                throw new HttpRequestException("Qobuz account credentials are incomplete.", null, HttpStatusCode.Unauthorized);
            return operation(new QobuzProvider(_httpClient, _bundleService, _secrets, _logger, token.GetString()));
        }, context.CancellationToken);
    }

    public override Task<List<Song>> SearchSongsAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, limit, "track", "tracks", ParseQobuzTrack, "songs", cancellationToken);

    public override async Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        var results = await SearchSongsAsync(isrc, limit: 5, cancellationToken);
        return results.FirstOrDefault(song =>
            !string.IsNullOrWhiteSpace(song.Isrc) &&
            song.Isrc.Equals(isrc, StringComparison.OrdinalIgnoreCase));
    }

    public override Task<List<Album>> SearchAlbumsAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, limit, "album", "albums", ParseQobuzAlbum, "albums", cancellationToken);

    public override Task<List<Artist>> SearchArtistsAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        SearchAsync(query, limit, "artist", "artists", ParseQobuzArtist, "artists", cancellationToken);

    private async Task<List<T>> SearchAsync<T>(
        string query,
        int limit,
        string endpoint,
        string envelopeName,
        Func<JsonElement, T> parse,
        string resultKind,
        CancellationToken cancellationToken)
    {
        try
        {
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            var url = $"{BaseUrl}{endpoint}/search?query={Uri.EscapeDataString(query)}&limit={limit}&app_id={appId}";
            using var response = await GetWithAuthAsync(url, appId, cancellationToken);
            if (!response.IsSuccessStatusCode) return [];

            using var result = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (!result.RootElement.TryGetProperty(envelopeName, out var envelope) ||
                !envelope.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return [];

            return items.EnumerateArray().Select(parse).ToList();
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return [];
        }
    }

    public override async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "qobuz") return null;

        try
        {
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            var url = $"{BaseUrl}track/get?track_id={Uri.EscapeDataString(externalId)}&app_id={appId}";

            using var response = await GetWithAuthAsync(url, appId, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var trackDocument = JsonDocument.Parse(json);
            var track = trackDocument.RootElement;

            if (track.TryGetProperty("error", out _)) return null;

            var song = ParseQobuzTrackFull(track);

            return song;
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return null;
        }
    }

    public override async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "qobuz") return null;

        try
        {
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            var url = $"{BaseUrl}album/get?album_id={Uri.EscapeDataString(externalId)}&app_id={appId}";

            using var response = await GetWithAuthAsync(url, appId, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var albumDocument = JsonDocument.Parse(json);
            var albumElement = albumDocument.RootElement;

            if (albumElement.TryGetProperty("error", out _)) return null;

            var album = ParseQobuzAlbum(albumElement);

            album.Songs = await GetAlbumTracksAsync(album, appId, cancellationToken, albumElement);

            return album;
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return null;
        }
    }

    public override async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "qobuz") return null;

        try
        {
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            var url = $"{BaseUrl}artist/get?artist_id={Uri.EscapeDataString(externalId)}&app_id={appId}";

            using var response = await GetWithAuthAsync(url, appId, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var artistDocument = JsonDocument.Parse(json);
            var artist = artistDocument.RootElement;

            if (artist.TryGetProperty("error", out _)) return null;

            return ParseQobuzArtist(artist);
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return null;
        }
    }

    public override async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "qobuz") return new List<Album>();

        try
        {
            var albums = new List<Album>();
            var seenAlbumIds = new HashSet<string>(StringComparer.Ordinal);
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            int offset = 0;
            const int limit = 500;

            // Qobuz requires pagination for artist albums
            while (true)
            {
                var url = $"{BaseUrl}artist/get?artist_id={Uri.EscapeDataString(externalId)}&app_id={appId}&limit={limit}&offset={offset}&extra=albums";

                using var response = await GetWithAuthAsync(url, appId, cancellationToken);
                if (!response.IsSuccessStatusCode) break;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var result = JsonDocument.Parse(json);

                if (!result.RootElement.TryGetProperty("albums", out var albumsData) ||
                    !albumsData.TryGetProperty("items", out var items))
                {
                    break;
                }

                var itemsArray = items.EnumerateArray().ToList();
                if (itemsArray.Count == 0) break;

                foreach (var album in itemsArray)
                {
                    var parsed = ParseQobuzAlbum(album);
                    if (seenAlbumIds.Add(parsed.ExternalId ?? parsed.Id)) albums.Add(parsed);
                }

                offset += itemsArray.Count;
                var total = albumsData.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out var value)
                    ? value
                    : (int?)null;
                if (total.HasValue ? offset >= total : itemsArray.Count < limit) break;
            }

            return albums;
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return new List<Album>();
        }
    }

    public override async Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "qobuz") return new List<Song>();

        try
        {
            var albums = await GetArtistAlbumsAsync(externalProvider, externalId, cancellationToken);
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            var songs = new List<Song>();
            var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var album in albums)
            {
                if (string.IsNullOrWhiteSpace(album.ExternalId)) continue;
                foreach (var song in await GetAlbumTracksAsync(album, appId, cancellationToken))
                    if (seenTrackIds.Add(song.ExternalId ?? song.Id)) songs.Add(song);
            }
            return songs;
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return new List<Song>();
        }
    }

    private async Task<List<Song>> GetAlbumTracksAsync(
        Album listedAlbum, string appId, CancellationToken cancellationToken, JsonElement? firstPage = null)
    {
        var songs = new List<Song>();
        var offset = 0;
        for (var page = 0; page < MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonDocument? document = null;
            try
            {
                JsonElement root;
                if (page == 0 && firstPage.HasValue) root = firstPage.Value;
                else
                {
                    var url = $"{BaseUrl}album/get?album_id={Uri.EscapeDataString(listedAlbum.ExternalId!)}&app_id={appId}&limit={PageSize}&offset={offset}&extra=tracks";
                    using var response = await GetWithAuthAsync(url, appId, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (offset > 0) throw InvalidPaging();
                        return songs;
                    }
                    document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    root = document.RootElement;
                }
                if (root.TryGetProperty("error", out _) ||
                    !root.TryGetProperty("tracks", out var tracks) ||
                    !tracks.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    if (offset > 0) throw InvalidPaging();
                    return songs;
                }
                ValidateOffset(tracks, offset);
                var count = items.GetArrayLength();
                if (count == 0) break;
                var album = ParseQobuzAlbum(root);
                var largeArtwork = GetLargeCoverArtUrl(root);
                foreach (var track in items.EnumerateArray())
                {
                    var song = ParseQobuzTrack(track);
                    song.Album = album.Title;
                    song.AlbumId = album.Id;
                    song.AlbumArtist = album.Artist;
                    song.CoverArtUrl ??= album.CoverArtUrl;
                    song.CoverArtUrlLarge ??= largeArtwork;
                    if (string.IsNullOrWhiteSpace(song.Artist)) song.Artist = album.Artist;
                    song.ArtistId ??= album.ArtistId;
                    songs.Add(song);
                }
                offset += count;
                if (!HasNextPage(tracks, count, offset)) break;
                if (page == MaximumPages - 1) throw InvalidPaging();
            }
            finally { document?.Dispose(); }
        }
        return songs.OrderBy(song => song.DiscNumber ?? 1).ThenBy(song => song.Track ?? int.MaxValue).ToList();
    }

    private static bool HasNextPage(JsonElement envelope, int count, int offset)
    {
        var limit = envelope.TryGetProperty("limit", out var pageLimit) && pageLimit.TryGetInt32(out var reportedLimit) &&
                    reportedLimit > 0 ? Math.Min(reportedLimit, PageSize) : PageSize;
        return count >= limit ||
               (envelope.TryGetProperty("total", out var total) && total.TryGetInt32(out var value) && offset < value);
    }

    private static void ValidateOffset(JsonElement envelope, int offset)
    {
        if (envelope.TryGetProperty("offset", out var value) &&
            (!value.TryGetInt32(out var actual) || actual != offset)) throw InvalidPaging();
    }

    private static HttpRequestException InvalidPaging() =>
        new("Qobuz returned inconsistent pagination.", null, HttpStatusCode.BadGateway);

    public override async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        try
        {
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            var url = $"{BaseUrl}playlist/search?query={Uri.EscapeDataString(query)}&limit={limit}&app_id={appId}";

            using var response = await GetWithAuthAsync(url, appId, cancellationToken);
            if (!response.IsSuccessStatusCode) return new List<ExternalPlaylist>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var result = JsonDocument.Parse(json);

            var playlists = new List<ExternalPlaylist>();
            if (result.RootElement.TryGetProperty("playlists", out var playlistsData) &&
                playlistsData.TryGetProperty("items", out var items))
            {
                foreach (var playlist in items.EnumerateArray())
                {
                    playlists.Add(ParseQobuzPlaylist(playlist));
                }
            }

            return playlists;
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return new List<ExternalPlaylist>();
        }
    }

    public override async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "qobuz") return null;

        try
        {
            var appId = await _bundleService.GetAppIdAsync(cancellationToken);
            var url = $"{BaseUrl}playlist/get?playlist_id={Uri.EscapeDataString(externalId)}&app_id={appId}";

            using var response = await GetWithAuthAsync(url, appId, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var playlistDocument = JsonDocument.Parse(json);
            var playlistElement = playlistDocument.RootElement;

            if (playlistElement.TryGetProperty("error", out _)) return null;

            return ParseQobuzPlaylist(playlistElement);
        }
        catch (Exception ex) when (ShouldHandle(ex, cancellationToken))
        {
            _logger.LogWarning("Qobuz catalog response could not be read.");
            return null;
        }
    }

    public override async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != StableProviderId) return [];
        var appId = await _bundleService.GetAppIdAsync(cancellationToken);
        var songs = new List<Song>();
        for (var page = 0; page < MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = songs.Count;
            var url = $"{BaseUrl}playlist/get?playlist_id={Uri.EscapeDataString(externalId)}&app_id={appId}&extra=tracks&limit={PageSize}&offset={offset}";
            using var response = await GetWithAuthAsync(url, appId, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (offset > 0) throw InvalidPaging();
                return songs;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _) || !root.TryGetProperty("tracks", out var tracks) ||
                !tracks.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                if (offset > 0) throw InvalidPaging();
                return songs;
            }
            ValidateOffset(tracks, offset);
            var count = items.GetArrayLength();
            if (count == 0) return songs;
            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            foreach (var track in items.EnumerateArray())
            {
                var song = ParseQobuzTrack(track);
                song.Album = name ?? "Unknown Playlist";
                song.Track = songs.Count + 1;
                song.DiscNumber = null;
                songs.Add(song);
            }
            if (!HasNextPage(tracks, count, songs.Count)) return songs;
        }
        throw InvalidPaging();
    }

    private static bool ShouldHandle(Exception exception, CancellationToken cancellationToken) =>
        exception is not (OperationCanceledException or HttpRequestException);

    private ExternalPlaylist ParseQobuzPlaylist(JsonElement playlist)
    {
        var externalId = GetIdAsString(playlist.GetProperty("id"));

        string? curatorName = null;
        if (playlist.TryGetProperty("owner", out var owner) &&
            owner.TryGetProperty("name", out var ownerName))
        {
            curatorName = ownerName.GetString();
        }

        DateTime? createdDate = null;
        if (playlist.TryGetProperty("created_at", out var createdAtEl))
        {
            var timestamp = createdAtEl.GetInt64();
            createdDate = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
        }

        string? coverUrl = null;
        if (playlist.TryGetProperty("images300", out var images300))
        {
            var imagesArray = images300.EnumerateArray().ToList();
            if (imagesArray.Count > 0)
            {
                coverUrl = imagesArray[0].GetString();
            }
        }
        else if (playlist.TryGetProperty("image_rectangle", out var imageRect))
        {
            var imagesArray = imageRect.EnumerateArray().ToList();
            if (imagesArray.Count > 0)
            {
                coverUrl = imagesArray[0].GetString();
            }
        }

        return new ExternalPlaylist
        {
            Id = PlaylistIdHelper.CreatePlaylistId("qobuz", externalId),
            Name = playlist.TryGetProperty("name", out var name)
                ? name.GetString() ?? ""
                : "",
            Description = playlist.TryGetProperty("description", out var desc)
                ? desc.GetString()
                : null,
            CuratorName = curatorName,
            Provider = "qobuz",
            ExternalId = externalId,
            TrackCount = playlist.TryGetProperty("tracks_count", out var tracksCount)
                ? tracksCount.GetInt32()
                : 0,
            Duration = playlist.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : 0,
            CoverUrl = coverUrl,
            CreatedDate = createdDate
        };
    }

    private async Task<HttpResponseMessage> GetWithAuthAsync(string url, string appId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.Add("X-App-Id", appId);

        if (!string.IsNullOrEmpty(_userAuthToken))
        {
            request.Headers.Add("X-User-Auth-Token", _userAuthToken);
        }

        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException("Qobuz catalog request failed.", null, status);
    }

    private Song ParseQobuzTrack(JsonElement track)
    {
        var externalId = GetIdAsString(track.GetProperty("id"));

        var title = track.GetProperty("title").GetString() ?? "";

        if (track.TryGetProperty("version", out var version))
        {
            var versionStr = version.GetString();
            if (!string.IsNullOrEmpty(versionStr))
            {
                title = $"{title} ({versionStr})";
            }
        }

        if (track.TryGetProperty("work", out var work))
        {
            var workStr = work.GetString();
            if (!string.IsNullOrEmpty(workStr))
            {
                title = $"{workStr}: {title}";
            }
        }

        var performerName = track.TryGetProperty("performer", out var performer)
            ? performer.GetProperty("name").GetString() ?? ""
            : "";

        var albumTitle = track.TryGetProperty("album", out var album)
            ? album.GetProperty("title").GetString() ?? ""
            : "";

        var albumId = track.TryGetProperty("album", out var albumForId)
            ? BuildExternalAlbumId("qobuz", GetIdAsString(albumForId.GetProperty("id")))
            : null;

        var albumArtist = track.TryGetProperty("album", out var albumForArtist) &&
                          albumForArtist.TryGetProperty("artist", out var albumArtistEl)
            ? albumArtistEl.GetProperty("name").GetString()
            : performerName;

        return new Song
        {
            Id = BuildExternalSongId("qobuz", externalId),
            Title = title,
            Artist = performerName,
            ArtistId = track.TryGetProperty("performer", out var performerForId)
                ? BuildExternalArtistId("qobuz", GetIdAsString(performerForId.GetProperty("id")))
                : null,
            Album = albumTitle,
            AlbumId = albumId,
            AlbumArtist = albumArtist,
            Duration = track.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : null,
            Track = track.TryGetProperty("track_number", out var trackNum)
                ? trackNum.GetInt32()
                : null,
            DiscNumber = track.TryGetProperty("media_number", out var mediaNum)
                ? mediaNum.GetInt32()
                : null,
            CoverArtUrl = GetCoverArtUrl(track),
            IsLocal = false,
            ExternalProvider = "qobuz",
            ExternalId = externalId
        };
    }

    private Song ParseQobuzTrackFull(JsonElement track)
    {
        var song = ParseQobuzTrack(track);

        if (track.TryGetProperty("composer", out var composer) &&
            composer.TryGetProperty("name", out var composerName))
        {
            song.Contributors = new List<string> { composerName.GetString() ?? "" };
        }

        if (track.TryGetProperty("isrc", out var isrc))
        {
            song.Isrc = isrc.GetString();
        }

        if (track.TryGetProperty("copyright", out var copyright))
        {
            song.Copyright = FormatCopyright(copyright.GetString() ?? "");
        }

        if (track.TryGetProperty("album", out var album))
        {
            if (album.TryGetProperty("release_date_original", out var releaseDate))
            {
                var dateStr = releaseDate.GetString();
                song.ReleaseDate = dateStr;

                song.Year = ParseYearFromDateString(dateStr);
            }

            if (album.TryGetProperty("tracks_count", out var tracksCount))
            {
                song.TotalTracks = tracksCount.GetInt32();
            }

            if (album.TryGetProperty("genres_list", out var genres))
            {
                song.Genre = FormatGenres(genres);
            }

            song.CoverArtUrlLarge = GetLargeCoverArtUrl(album);
        }

        return song;
    }

    private Album ParseQobuzAlbum(JsonElement album)
    {
        var externalId = GetIdAsString(album.GetProperty("id"));

        var title = album.GetProperty("title").GetString() ?? "";

        if (album.TryGetProperty("version", out var version))
        {
            var versionStr = version.GetString();
            if (!string.IsNullOrEmpty(versionStr))
            {
                title = $"{title} ({versionStr})";
            }
        }

        var artistName = album.TryGetProperty("artist", out var artist)
            ? artist.GetProperty("name").GetString() ?? ""
            : "";

        int? year = null;
        if (album.TryGetProperty("release_date_original", out var releaseDate))
        {
            var dateStr = releaseDate.GetString();
            year = ParseYearFromDateString(dateStr);
        }

        return new Album
        {
            Id = BuildExternalAlbumId("qobuz", externalId),
            Title = title,
            Artist = artistName,
            ArtistId = album.TryGetProperty("artist", out var artistForId)
                ? BuildExternalArtistId("qobuz", GetIdAsString(artistForId.GetProperty("id")))
                : null,
            Year = year,
            SongCount = album.TryGetProperty("tracks_count", out var tracksCount)
                ? tracksCount.GetInt32()
                : null,
            CoverArtUrl = GetCoverArtUrl(album),
            Genre = album.TryGetProperty("genres_list", out var genres)
                ? FormatGenres(genres)
                : null,
            IsLocal = false,
            ExternalProvider = "qobuz",
            ExternalId = externalId
        };
    }

    private Artist ParseQobuzArtist(JsonElement artist)
    {
        var externalId = GetIdAsString(artist.GetProperty("id"));

        return new Artist
        {
            Id = BuildExternalArtistId("qobuz", externalId),
            Name = artist.GetProperty("name").GetString() ?? "",
            ImageUrl = GetArtistImageUrl(artist),
            AlbumCount = artist.TryGetProperty("albums_count", out var albumsCount)
                ? albumsCount.GetInt32()
                : null,
            IsLocal = false,
            ExternalProvider = "qobuz",
            ExternalId = externalId
        };
    }

    private string? GetCoverArtUrl(JsonElement element)
    {
        if (element.TryGetProperty("album", out var album))
        {
            element = album;
        }

        if (element.TryGetProperty("image", out var image))
        {
            if (image.TryGetProperty("thumbnail", out var thumbnail))
            {
                return thumbnail.GetString();
            }
            if (image.TryGetProperty("small", out var small))
            {
                return small.GetString();
            }
        }

        return null;
    }

    private string? GetLargeCoverArtUrl(JsonElement album)
    {
        if (album.TryGetProperty("image", out var image) &&
            image.TryGetProperty("large", out var large))
        {
            var url = large.GetString();
            // Qobuz exposes original artwork through the _org size suffix.
            return url?.Replace("_600.jpg", "_org.jpg");
        }

        return null;
    }

    private string? GetArtistImageUrl(JsonElement artist)
    {
        if (artist.TryGetProperty("image", out var image) &&
            image.TryGetProperty("large", out var large))
        {
            return large.GetString();
        }

        return null;
    }

    // Qobuz encodes genres as hierarchical paths such as Pop/Rock→Alternative.
    private string FormatGenres(JsonElement genresList)
    {
        var genres = new List<string>();

        foreach (var genre in genresList.EnumerateArray())
        {
            var genreStr = genre.GetString();
            if (!string.IsNullOrEmpty(genreStr))
            {
                var parts = genreStr.Split(new[] { '/', '→' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!genres.Contains(trimmed))
                    {
                        genres.Add(trimmed);
                    }
                }
            }
        }

        return string.Join(", ", genres);
    }

    private string FormatCopyright(string copyright)
    {
        return copyright
            .Replace("(P)", "℗")
            .Replace("(C)", "©");
    }

}
