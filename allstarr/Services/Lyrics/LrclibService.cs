using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Storage;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Services.Lyrics;

public class LrclibService
{
    private readonly HttpClient _httpClient;
    private readonly IApplicationCache _cache;
    private readonly IManualLyricsMappingStore _mappingStore;
    private readonly ILogger<LrclibService> _logger;
    private const string BaseUrl = "https://lrclib.net/api";

    public LrclibService(
        IHttpClientFactory httpClientFactory,
        IApplicationCache cache,
        IManualLyricsMappingStore mappingStore,
        ILogger<LrclibService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Lrclib");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Allstarr/1.0.3 (https://github.com/SoPat712/allstarr)");
        _cache = cache;
        _mappingStore = mappingStore;
        _logger = logger;
    }

    public async Task<LyricsInfo?> GetLyricsAsync(string trackName, string artistName, string albumName, int durationSeconds)
    {
        return await GetLyricsAsync(trackName, new[] { artistName }, albumName, durationSeconds);
    }

    public async Task<LyricsInfo?> GetLyricsAsync(string trackName, string[] artistNames, string albumName, int durationSeconds)
    {
        // Validate input parameters
        if (string.IsNullOrWhiteSpace(trackName) || artistNames == null || artistNames.Length == 0)
        {
            _logger.LogDebug("Invalid parameters for lyrics search: trackName={TrackName}, artistCount={ArtistCount}",
                trackName, artistNames?.Length ?? 0);
            return null;
        }

        var artistName = string.Join(", ", artistNames);
        var cacheKey = CacheKeyBuilder.BuildLyricsKey(artistName, trackName, albumName, durationSeconds);

        // FIRST: Check for manual lyrics mapping
        var manualLyricsId = await _mappingStore.FindLyricsIdAsync(artistName, trackName);
        if (manualLyricsId is > 0)
        {
            _logger.LogInformation("✓ Manual lyrics mapping found for {Artist} - {Track}: Lyrics ID {Id}",
                artistName, trackName, manualLyricsId);

            // Fetch lyrics by ID
            var manualLyrics = await GetLyricsByIdAsync(manualLyricsId.Value);
            if (manualLyrics != null && !string.IsNullOrEmpty(manualLyrics.PlainLyrics))
            {
                // Cache the lyrics using the standard cache key
                await _cache.SetAsync(cacheKey, manualLyrics.PlainLyrics!, CacheExtensions.LyricsTTL);
                return manualLyrics;
            }
            else
            {
                _logger.LogWarning("Manual lyrics mapping points to invalid ID {Id} for {Artist} - {Track}",
                    manualLyricsId, artistName, trackName);
            }
        }

        // SECOND: Check standard cache
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
            // Try searching with all artists joined (space-separated for better matching)
            var searchArtistName = string.Join(" ", artistNames);

            // First try search API for fuzzy matching (more forgiving)
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
                    // Find best match by comparing track name, artist, and duration
                    LrclibResponse? bestMatch = null;
                    double bestScore = 0;

                    foreach (var result in searchResults)
                    {
                        // Calculate similarity scores
                        var trackScore = CalculateSimilarity(trackName, result.TrackName ?? "");

                        // Count artists in the result
                        var resultArtistCount = CountArtists(result.ArtistName ?? "");
                        var expectedArtistCount = artistNames.Length;

                        // Artist matching - check if all our artists are present
                        var artistScore = CalculateArtistSimilarity(artistNames, result.ArtistName ?? "");

                        // STRONG bonus for matching artist count (this is critical!)
                        var artistCountBonus = resultArtistCount == expectedArtistCount ? 50.0 : 0.0;

                        // Duration match (within 5 seconds is good)
                        var durationDiff = result.Duration.HasValue ? Math.Abs(result.Duration.Value - durationSeconds) : 999;
                        var durationScore = durationDiff <= 5 ? 100.0 : Math.Max(0, 100 - (durationDiff * 2));

                        // Bonus for having synced lyrics (prefer synced over plain)
                        var syncedBonus = !string.IsNullOrEmpty(result.SyncedLyrics) ? 15.0 : 0.0;

                        // Weighted score: track name important, artist match critical, artist count VERY important
                        var totalScore = (trackScore * 0.3) + (artistScore * 0.3) + (durationScore * 0.15) + artistCountBonus + syncedBonus;

                        _logger.LogDebug("Candidate: {Track} by {Artist} ({ArtistCount} artists) - Score: {Score:F1} (track:{TrackScore:F1}, artist:{ArtistScore:F1}, duration:{DurationScore:F1}, countBonus:{CountBonus:F1}, synced:{Synced})",
                            result.TrackName, result.ArtistName, resultArtistCount, totalScore, trackScore, artistScore, durationScore, artistCountBonus, !string.IsNullOrEmpty(result.SyncedLyrics));

                        if (totalScore > bestScore)
                        {
                            bestScore = totalScore;
                            bestMatch = result;
                        }
                    }

                    // Only use result if score is good enough (>60%)
                    if (bestMatch != null && bestScore >= 60)
                    {
                        _logger.LogInformation("✓ Found lyrics via search for {Artist} - {Track} (ID: {Id}, score: {Score:F1}, synced: {HasSynced})",
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

                        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result, JsonOptions), CacheExtensions.LyricsTTL);
                        return result;
                    }
                    else
                    {
                        _logger.LogDebug("Best match score too low ({Score:F1}), trying exact match", bestScore);
                    }
                }
            }

            // Fall back to exact match API if search didn't find good results
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

            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(exactResult, JsonOptions), CacheExtensions.LyricsTTL);

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

    /// <summary>
    /// Counts the number of artists in an artist string (separated by comma, ampersand, or 'e')
    /// </summary>
    private static int CountArtists(string artistString)
    {
        if (string.IsNullOrWhiteSpace(artistString))
            return 0;

        // Split by common separators: comma, ampersand, " e " (Portuguese/Spanish "and")
        var separators = new[] { ',', '&' };
        var parts = artistString.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        // Also check for " e " pattern (like "Julia Michaels e Alessia Cara")
        var count = parts.Length;
        foreach (var part in parts)
        {
            if (part.Contains(" e ", StringComparison.OrdinalIgnoreCase))
            {
                count += part.Split(new[] { " e " }, StringSplitOptions.RemoveEmptyEntries).Length - 1;
            }
        }

        return Math.Max(1, count);
    }

    /// <summary>
    /// Calculates how well the expected artists match the result's artist string
    /// </summary>
    private static double CalculateArtistSimilarity(string[] expectedArtists, string resultArtistString)
    {
        if (expectedArtists.Length == 0 || string.IsNullOrWhiteSpace(resultArtistString))
            return 0;

        var resultLower = resultArtistString.ToLowerInvariant();
        var matchedCount = 0;

        foreach (var artist in expectedArtists)
        {
            var artistLower = artist.ToLowerInvariant();

            // Check if this artist appears in the result string
            if (resultLower.Contains(artistLower))
            {
                matchedCount++;
            }
            else
            {
                // Try token-based matching for partial matches
                var artistTokens = artistLower.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                var matchedTokens = artistTokens.Count(token => resultLower.Contains(token));

                // If most tokens match, count it as a partial match
                if (matchedTokens >= artistTokens.Length * 0.7)
                {
                    matchedCount++;
                }
            }
        }

        // Return percentage of artists matched
        return (matchedCount * 100.0) / expectedArtists.Length;
    }

    private static double CalculateSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
            return 0;

        str1 = str1.ToLowerInvariant();
        str2 = str2.ToLowerInvariant();

        if (str1 == str2)
            return 100;

        // Simple token-based matching
        var tokens1 = str1.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var tokens2 = str2.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        if (tokens1.Length == 0 || tokens2.Length == 0)
            return 0;

        var matchedTokens = tokens1.Count(t1 => tokens2.Any(t2 => t2.Contains(t1) || t1.Contains(t2)));
        return (matchedTokens * 100.0) / Math.Max(tokens1.Length, tokens2.Length);
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

            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result, JsonOptions), CacheExtensions.LyricsTTL);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics by ID {Id}", id);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
