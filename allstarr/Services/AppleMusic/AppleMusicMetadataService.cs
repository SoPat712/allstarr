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
        if (!TryEndpoint($"api/search?q={Uri.EscapeDataString(query)}&type=song&limit={limit}", out var url)) return [];
        try
        {
            var results = await _httpClient.GetFromJsonAsync<List<GamdlSongResult>>(url, cancellationToken);
            
            if (results == null) return new List<Song>();

            return results.Select(r => new Song
            {
                Id = $"ext-applemusic-song-{r.Id}",
                Title = r.Title,
                Artist = r.Artist,
                Artists = new List<string> { r.Artist },
                Album = r.Album,
                Duration = r.Duration,
                Track = r.TrackNumber,
                CoverArtUrl = r.CoverUrl,
                CoverArtUrlLarge = r.CoverUrl,
                Isrc = r.Isrc,
                ExternalProvider = "applemusic",
                ExternalId = r.Id,
                IsLocal = false
            }).ToList();
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
        if (externalProvider != "applemusic" ||
            !TryEndpoint($"api/song/{Uri.EscapeDataString(externalId)}", out var url)) return null;

        try
        {
            var r = await _httpClient.GetFromJsonAsync<GamdlSongDetail>(url, cancellationToken);
            
            if (r == null) return null;

            return new Song
            {
                Id = $"ext-applemusic-song-{r.Id}",
                Title = r.Title,
                Artist = r.Artist,
                Artists = new List<string> { r.Artist },
                Album = r.Album,
                Duration = r.Duration,
                Track = r.TrackNumber,
                DiscNumber = r.DiscNumber,
                CoverArtUrl = r.CoverUrl,
                CoverArtUrlLarge = r.CoverUrl,
                Isrc = r.Isrc,
                ReleaseDate = r.ReleaseDate,
                Copyright = r.Copyright,
                Composer = r.Composer,
                Genre = r.Genre,
                ExternalProvider = "applemusic",
                ExternalId = r.Id,
                IsLocal = false
            };
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
        await Task.CompletedTask;
        return null;
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return null;
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        return new List<Album>();
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

    // --- JSON Mapping Helper Classes ---
    
    private class GamdlSongResult
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("artist")] public string Artist { get; set; } = string.Empty;
        [JsonPropertyName("album")] public string Album { get; set; } = string.Empty;
        [JsonPropertyName("duration")] public int Duration { get; set; }
        [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("track_number")] public int? TrackNumber { get; set; }
        [JsonPropertyName("isrc")] public string? Isrc { get; set; }
    }

    private class GamdlAlbumResult
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("artist")] public string Artist { get; set; } = string.Empty;
        [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    }

    private class GamdlArtistResult
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    private class GamdlSongDetail
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("artist")] public string Artist { get; set; } = string.Empty;
        [JsonPropertyName("album")] public string Album { get; set; } = string.Empty;
        [JsonPropertyName("duration")] public int Duration { get; set; }
        [JsonPropertyName("track_number")] public int? TrackNumber { get; set; }
        [JsonPropertyName("disc_number")] public int? DiscNumber { get; set; }
        [JsonPropertyName("isrc")] public string? Isrc { get; set; }
        [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("copyright")] public string? Copyright { get; set; }
        [JsonPropertyName("composer")] public string? Composer { get; set; }
        [JsonPropertyName("genre")] public string? Genre { get; set; }
    }
}
