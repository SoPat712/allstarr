using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Storage;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Lyrics;

public class LrclibService
{
    private readonly HttpClient _httpClient;
    private readonly IApplicationCache _cache;
    private readonly IManualLyricsMappingStore _mappingStore;
    private readonly ILogger<LrclibService> _logger;
    private readonly TimeSpan _lyricsTtl;
    private const string BaseUrl = "https://lrclib.net/api";

    public LrclibService(
        IHttpClientFactory httpClientFactory,
        IApplicationCache cache,
        IManualLyricsMappingStore mappingStore,
        IOptions<CacheSettings> cacheSettings,
        ILogger<LrclibService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Lrclib");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AppIdentity.UserAgent);
        _cache = cache;
        _mappingStore = mappingStore;
        _lyricsTtl = cacheSettings.Value.LyricsTTL;
        _logger = logger;
    }

    public Task<LyricsInfo?> GetLyricsAsync(string trackName, string artistName, string albumName, int durationSeconds) =>
        GetLyricsAsync(trackName, [artistName], albumName, durationSeconds);

    public async Task<LyricsInfo?> GetLyricsAsync(string trackName, string[] artistNames, string albumName, int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(trackName) || artistNames == null || artistNames.Length == 0)
        {
            _logger.LogDebug("Invalid parameters for lyrics search: trackName={TrackName}, artistCount={ArtistCount}",
                trackName, artistNames?.Length ?? 0);
            return null;
        }

        var artistName = string.Join(", ", artistNames);
        var cacheKey = CacheKeyBuilder.BuildLyricsKey(artistName, trackName, albumName, durationSeconds);

        var manualLyricsId = await _mappingStore.FindLyricsIdAsync(artistName, trackName);
        if (manualLyricsId is > 0)
        {
            _logger.LogInformation("Manual lyrics mapping found for {Artist} - {Track}: Lyrics ID {Id}",
                artistName, trackName, manualLyricsId);

            var manualLyrics = await GetLyricsByIdAsync(manualLyricsId.Value);
            if (manualLyrics != null && !string.IsNullOrEmpty(manualLyrics.PlainLyrics))
            {
                await CacheLyricsAsync(cacheKey, manualLyrics);
                return manualLyrics;
            }

            _logger.LogWarning("Manual lyrics mapping points to invalid ID {Id} for {Artist} - {Track}",
                manualLyricsId, artistName, trackName);
        }

        var cached = await _cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try
            {
                return JsonSerializer.Deserialize<LyricsInfo>(cached, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize cached lyrics");
            }
        }

        try
        {
            var searchArtistName = string.Join(" ", artistNames);

            var searchUrl = $"{BaseUrl}/search?" +
                           $"track_name={Uri.EscapeDataString(trackName)}&" +
                           $"artist_name={Uri.EscapeDataString(searchArtistName)}";

            _logger.LogDebug("Searching LRCLIB: {Url} (expecting {ArtistCount} artists)", searchUrl, artistNames.Length);

            using var searchResponse = await _httpClient.GetAsync(searchUrl);

            if (searchResponse.IsSuccessStatusCode)
            {
                var searchJson = await searchResponse.Content.ReadAsStringAsync();
                var searchResults = JsonSerializer.Deserialize<List<LrclibResponse>>(searchJson, JsonOptions);

                if (searchResults != null && searchResults.Count > 0)
                {
                    LrclibResponse? bestMatch = null;
                    double bestScore = 0;

                    foreach (var result in searchResults)
                    {
                        var trackScore = FuzzyMatcher.CalculateSimilarity(trackName, result.TrackName ?? "");

                        var resultArtistCount = CountArtists(result.ArtistName ?? "");
                        var expectedArtistCount = artistNames.Length;
                        var artistScore = CalculateArtistSimilarity(artistNames, result.ArtistName ?? "");
                        var artistCountBonus = resultArtistCount == expectedArtistCount ? 50.0 : 0.0;
                        var durationDiff = result.Duration.HasValue ? Math.Abs(result.Duration.Value - durationSeconds) : 999;
                        var durationScore = durationDiff <= 5 ? 100.0 : Math.Max(0, 100 - (durationDiff * 2));
                        var syncedBonus = !string.IsNullOrEmpty(result.SyncedLyrics) ? 15.0 : 0.0;
                        var totalScore = (trackScore * 0.3) + (artistScore * 0.3) + (durationScore * 0.15) + artistCountBonus + syncedBonus;

                        _logger.LogDebug("Candidate: {Track} by {Artist} ({ArtistCount} artists) - Score: {Score:F1} (track:{TrackScore:F1}, artist:{ArtistScore:F1}, duration:{DurationScore:F1}, countBonus:{CountBonus:F1}, synced:{Synced})",
                            result.TrackName, result.ArtistName, resultArtistCount, totalScore, trackScore, artistScore, durationScore, artistCountBonus, !string.IsNullOrEmpty(result.SyncedLyrics));

                        if (totalScore > bestScore)
                        {
                            bestScore = totalScore;
                            bestMatch = result;
                        }
                    }

                    if (bestMatch != null && bestScore >= 60)
                    {
                        _logger.LogInformation("Found lyrics via search for {Artist} - {Track} (ID: {Id}, score: {Score:F1}, synced: {HasSynced})",
                            artistName, trackName, bestMatch.Id, bestScore, !string.IsNullOrEmpty(bestMatch.SyncedLyrics));

                        var result = new LyricsInfo
                        {
                            Id = bestMatch.Id,
                            TrackName = bestMatch.TrackName ?? trackName,
                            ArtistName = bestMatch.ArtistName ?? artistName,
                            AlbumName = bestMatch.AlbumName ?? albumName,
                            Duration = bestMatch.Duration.HasValue ? (int)Math.Round(bestMatch.Duration.Value) : durationSeconds,
                            Instrumental = bestMatch.Instrumental,
                            PlainLyrics = bestMatch.PlainLyrics,
                            SyncedLyrics = bestMatch.SyncedLyrics
                        };

                        await CacheLyricsAsync(cacheKey, result);
                        return result;
                    }
                    else
                    {
                        _logger.LogDebug("Best match score too low ({Score:F1}), trying exact match", bestScore);
                    }
                }
            }

            var exactUrl = $"{BaseUrl}/get?" +
                          $"track_name={Uri.EscapeDataString(trackName)}&" +
                          $"artist_name={Uri.EscapeDataString(artistName)}&" +
                          $"album_name={Uri.EscapeDataString(albumName)}&" +
                          $"duration={durationSeconds}";

            _logger.LogDebug("Trying exact match from LRCLIB: {Url}", exactUrl);

            using var exactResponse = await _httpClient.GetAsync(exactUrl);

            if (exactResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Lyrics not found for {Artist} - {Track}", artistName, trackName);
                return null;
            }

            exactResponse.EnsureSuccessStatusCode();

            var json = await exactResponse.Content.ReadAsStringAsync();
            var lyrics = JsonSerializer.Deserialize<LrclibResponse>(json, JsonOptions);

            if (lyrics == null)
            {
                return null;
            }

            var exactResult = new LyricsInfo
            {
                Id = lyrics.Id,
                TrackName = lyrics.TrackName ?? trackName,
                ArtistName = lyrics.ArtistName ?? artistName,
                AlbumName = lyrics.AlbumName ?? albumName,
                Duration = lyrics.Duration.HasValue ? (int)Math.Round(lyrics.Duration.Value) : durationSeconds,
                Instrumental = lyrics.Instrumental,
                PlainLyrics = lyrics.PlainLyrics,
                SyncedLyrics = lyrics.SyncedLyrics
            };

            await CacheLyricsAsync(cacheKey, exactResult);

            _logger.LogInformation("Retrieved lyrics via exact match for {Artist} - {Track} (ID: {Id})", artistName, trackName, lyrics.Id);

            return exactResult;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch lyrics from LRCLIB for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    private static int CountArtists(string artistString)
    {
        if (string.IsNullOrWhiteSpace(artistString))
            return 0;

        var count = artistString
            .Split([',', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Sum(part => part.Split(" e ", StringSplitOptions.RemoveEmptyEntries).Length);
        return Math.Max(1, count);
    }

    private static double CalculateArtistSimilarity(string[] expectedArtists, string resultArtistString)
    {
        if (expectedArtists.Length == 0 || string.IsNullOrWhiteSpace(resultArtistString))
            return 0;

        var resultLower = resultArtistString.ToLowerInvariant();
        var matchedCount = 0;

        foreach (var artist in expectedArtists)
        {
            var artistLower = artist.ToLowerInvariant();

            if (resultLower.Contains(artistLower))
            {
                matchedCount++;
            }
            else
            {
                var artistTokens = artistLower.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
                var matchedTokens = artistTokens.Count(token => resultLower.Contains(token));

                if (matchedTokens >= artistTokens.Length * 0.7)
                {
                    matchedCount++;
                }
            }
        }

        return (matchedCount * 100.0) / expectedArtists.Length;
    }

    public async Task<LyricsInfo?> GetLyricsCachedAsync(string trackName, string artistName, string albumName, int durationSeconds)
    {
        try
        {
            var url = $"{BaseUrl}/get-cached?" +
                     $"track_name={Uri.EscapeDataString(trackName)}&" +
                     $"artist_name={Uri.EscapeDataString(artistName)}&" +
                     $"album_name={Uri.EscapeDataString(albumName)}&" +
                     $"duration={durationSeconds}";

            using var response = await _httpClient.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var lyrics = JsonSerializer.Deserialize<LrclibResponse>(json, JsonOptions);

            if (lyrics == null)
            {
                return null;
            }

            return new LyricsInfo
            {
                Id = lyrics.Id,
                TrackName = lyrics.TrackName ?? trackName,
                ArtistName = lyrics.ArtistName ?? artistName,
                AlbumName = lyrics.AlbumName ?? albumName,
                Duration = lyrics.Duration.HasValue ? (int)Math.Round(lyrics.Duration.Value) : durationSeconds,
                Instrumental = lyrics.Instrumental,
                PlainLyrics = lyrics.PlainLyrics,
                SyncedLyrics = lyrics.SyncedLyrics
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch cached lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    public async Task<LyricsInfo?> GetLyricsByIdAsync(int id)
    {
        var cacheKey = CacheKeyBuilder.BuildLyricsByIdKey(id);

        var cached = await _cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try
            {
                return JsonSerializer.Deserialize<LyricsInfo>(cached, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize cached lyrics");
            }
        }

        try
        {
            var url = $"{BaseUrl}/get/{id}";
            using var response = await _httpClient.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var lyrics = JsonSerializer.Deserialize<LrclibResponse>(json, JsonOptions);

            if (lyrics == null)
            {
                return null;
            }

            var result = new LyricsInfo
            {
                Id = lyrics.Id,
                TrackName = lyrics.TrackName ?? string.Empty,
                ArtistName = lyrics.ArtistName ?? string.Empty,
                AlbumName = lyrics.AlbumName ?? string.Empty,
                Duration = lyrics.Duration.HasValue ? (int)Math.Round(lyrics.Duration.Value) : 0,
                Instrumental = lyrics.Instrumental,
                PlainLyrics = lyrics.PlainLyrics,
                SyncedLyrics = lyrics.SyncedLyrics
            };

            await CacheLyricsAsync(cacheKey, result);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics by ID {Id}", id);
            return null;
        }
    }

    public Task CacheLyricsAsync(string cacheKey, LyricsInfo lyrics) =>
        _cache.SetStringAsync(
            cacheKey,
            JsonSerializer.Serialize(lyrics, JsonOptions),
            _lyricsTtl);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class LrclibResponse
    {
        public int Id { get; set; }
        public string? TrackName { get; set; }
        public string? ArtistName { get; set; }
        public string? AlbumName { get; set; }
        public double? Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
