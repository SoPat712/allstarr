using System.Net.Http.Json;
using System.Text.Json.Serialization;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.AppleMusic;

public class AppleMusicMetadataService : IConcreteMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly AppleDownloadSettings _settings;
    private readonly ILogger<AppleMusicMetadataService> _logger;

    public AppleMusicMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppleDownloadSettings> settings,
        ILogger<AppleMusicMetadataService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("AppleMusic");
        _settings = settings.Value;
        _logger = logger;

    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (!TryEndpoint($"api/search?q={Uri.EscapeDataString(query)}&type=song&limit={Math.Clamp(limit, 1, 100)}", out var url)) return [];
        try
        {
            var results = await _httpClient.GetFromJsonAsync<List<GamdlSong>>(url, cancellationToken);

            if (results == null) return new List<Song>();

            return results.Select(ToSong).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Apple Music songs for query: {Query}", query);
            return new List<Song>();
        }
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return [];
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return [];
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        var songsTask = SearchSongsAsync(query, songLimit, cancellationToken);
        var albumsTask = SearchAlbumsAsync(query, albumLimit, cancellationToken);
        var artistsTask = SearchArtistsAsync(query, artistLimit, cancellationToken);

        await Task.WhenAll(songsTask, albumsTask, artistsTask);

        return new SearchResult
        {
            Songs = songsTask.Result,
            Albums = albumsTask.Result,
            Artists = artistsTask.Result
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider is not ("applemusic" or "apple-download") ||
            !TryEndpoint($"api/song/{Uri.EscapeDataString(externalId)}", out var url)) return null;

        try
        {
            var r = await _httpClient.GetFromJsonAsync<GamdlSong>(url, cancellationToken);

            if (r == null) return null;

            return ToSong(r);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Apple Music song details for ID: {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        // Fallback to song search by ISRC if not explicitly supported via an endpoint
        var results = await SearchSongsAsync(isrc, 1, cancellationToken);
        return results.FirstOrDefault();
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedProvider(externalProvider) ||
            !TryEndpoint($"api/album/{Uri.EscapeDataString(externalId)}", out var url)) return null;

        try
        {
            var album = await _httpClient.GetFromJsonAsync<GamdlAlbum>(url, cancellationToken);
            return album == null ? null : ToAlbum(album);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Apple Music album for ID: {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedProvider(externalProvider) ||
            !TryEndpoint($"api/artist/{Uri.EscapeDataString(externalId)}", out var url)) return null;

        try
        {
            var artist = await _httpClient.GetFromJsonAsync<GamdlArtist>(url, cancellationToken);
            return artist == null ? null : new Artist
            {
                Id = $"ext-apple-download-artist-{artist.Id}",
                Name = artist.Name,
                ImageUrl = artist.ImageUrl,
                AlbumCount = artist.AlbumCount,
                ExternalProvider = "apple-download",
                ExternalId = artist.Id,
                IsLocal = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Apple Music artist for ID: {ExternalId}", externalId);
            return null;
        }
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedProvider(externalProvider) ||
            !TryEndpoint($"api/artist/{Uri.EscapeDataString(externalId)}/albums", out var url)) return [];

        try
        {
            var albums = await _httpClient.GetFromJsonAsync<List<GamdlAlbum>>(url, cancellationToken);
            return albums?.Select(ToAlbum).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Apple Music albums for artist ID: {ExternalId}", externalId);
            return [];
        }
    }

    public async Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return new List<Song>();
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        return new List<ExternalPlaylist>();
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return null;
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return new List<Song>();
    }

    private bool TryEndpoint(string relativePath, out Uri? endpoint)
    {
        endpoint = null;
        if (!allstarr.Services.Common.OutboundRequestGuard.TryCreateConfiguredServiceUri(
                _settings.BaseUrl, out var baseUri, out _))
        {
            return false;
        }

        endpoint = new Uri(baseUri!, relativePath);
        return true;
    }

    private static bool IsSupportedProvider(string provider) => provider is "applemusic" or "apple-download";

    private static Song ToSong(GamdlSong song)
    {
        var artistId = string.IsNullOrWhiteSpace(song.ArtistId)
            ? null
            : $"ext-apple-download-artist-{song.ArtistId}";
        return new Song
        {
            Id = $"ext-apple-download-song-{song.Id}",
            Title = song.Title,
            Artist = song.Artist,
            ArtistId = artistId,
            Artists = [song.Artist],
            ArtistIds = artistId == null ? [] : [artistId],
            Album = song.Album,
            AlbumId = string.IsNullOrWhiteSpace(song.AlbumId)
                ? null
                : $"ext-apple-download-album-{song.AlbumId}",
            Duration = song.Duration,
            Track = song.TrackNumber,
            DiscNumber = song.DiscNumber,
            TotalTracks = song.TotalTracks,
            Year = Year(song.ReleaseDate),
            CoverArtUrl = song.CoverUrl,
            CoverArtUrlLarge = song.CoverUrl,
            Isrc = song.Isrc,
            ReleaseDate = song.ReleaseDate,
            Copyright = song.Copyright,
            Composer = song.Composer,
            Genre = song.Genre,
            ExternalProvider = "apple-download",
            ExternalId = song.Id,
            IsLocal = false
        };
    }

    private static Album ToAlbum(GamdlAlbum album) => new()
    {
        Id = $"ext-apple-download-album-{album.Id}",
        Title = album.Title,
        Artist = album.Artist,
        ArtistId = string.IsNullOrWhiteSpace(album.ArtistId)
            ? null
            : $"ext-apple-download-artist-{album.ArtistId}",
        Year = Year(album.ReleaseDate),
        SongCount = album.TrackCount,
        CoverArtUrl = album.CoverUrl,
        Genre = album.Genre,
        ExternalProvider = "apple-download",
        ExternalId = album.Id,
        IsLocal = false,
        Songs = album.Tracks.Select(ToSong).ToList()
    };

    private static int? Year(string? releaseDate) =>
        DateTimeOffset.TryParse(releaseDate, out var parsed) ? parsed.Year : null;

    // --- JSON Mapping Helper Classes ---

    private class GamdlSong
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("artist")] public string Artist { get; set; } = string.Empty;
        [JsonPropertyName("artist_id")] public string ArtistId { get; set; } = string.Empty;
        [JsonPropertyName("album")] public string Album { get; set; } = string.Empty;
        [JsonPropertyName("album_id")] public string AlbumId { get; set; } = string.Empty;
        [JsonPropertyName("duration")] public int Duration { get; set; }
        [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("track_number")] public int? TrackNumber { get; set; }
        [JsonPropertyName("disc_number")] public int? DiscNumber { get; set; }
        [JsonPropertyName("total_tracks")] public int? TotalTracks { get; set; }
        [JsonPropertyName("isrc")] public string? Isrc { get; set; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("copyright")] public string? Copyright { get; set; }
        [JsonPropertyName("composer")] public string? Composer { get; set; }
        [JsonPropertyName("genre")] public string? Genre { get; set; }
    }

    private class GamdlAlbum
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("artist")] public string Artist { get; set; } = string.Empty;
        [JsonPropertyName("artist_id")] public string ArtistId { get; set; } = string.Empty;
        [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("track_count")] public int? TrackCount { get; set; }
        [JsonPropertyName("genre")] public string? Genre { get; set; }
        [JsonPropertyName("tracks")] public List<GamdlSong> Tracks { get; set; } = [];
    }

    private class GamdlArtist
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
        [JsonPropertyName("album_count")] public int? AlbumCount { get; set; }
    }
}
