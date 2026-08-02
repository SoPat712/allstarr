using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.MusicBrainz;

/// <summary>
/// Service for querying MusicBrainz API for metadata enrichment.
/// </summary>
public class MusicBrainzService
{
    private readonly HttpClient _httpClient;
    private readonly MusicBrainzSettings _settings;
    private readonly CacheSettings _cacheSettings;
    private readonly IApplicationCache _cache;
    private readonly ILogger<MusicBrainzService> _logger;
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);

    public MusicBrainzService(
        IHttpClientFactory httpClientFactory,
        IOptions<MusicBrainzSettings> settings,
        IOptions<CacheSettings> cacheSettings,
        IApplicationCache cache,
        ILogger<MusicBrainzService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Allstarr/1.0.3 (https://github.com/SoPat712/allstarr)");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _settings = settings.Value;
        _cacheSettings = cacheSettings.Value;
        _cache = cache;
        _logger = logger;

        // Set up digest authentication if credentials provided
        if (!string.IsNullOrEmpty(_settings.Username) && !string.IsNullOrEmpty(_settings.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_settings.Username}:{_settings.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <summary>
    /// Looks up a recording by ISRC code.
    /// </summary>
    public async Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc)
    {
        if (!_settings.Enabled)
        {
            return null;
        }

        // Check cache first
        var cacheKey = CacheKeyBuilder.BuildMusicBrainzIsrcKey(isrc);
        var cached = await _cache.GetAsync<MusicBrainzRecording>(cacheKey);
        if (cached != null)
        {
            _logger.LogDebug("MusicBrainz ISRC cache hit: {Isrc}", isrc);
            return cached;
        }

        await RateLimitAsync();

        try
        {
            var url = $"{_settings.BaseUrl}/isrc/{isrc}?fmt=json&inc=artists+releases+release-groups+genres+tags";
            _logger.LogDebug("MusicBrainz ISRC lookup: {Url}", url);

            using var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz ISRC lookup failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<MusicBrainzIsrcResponse>(json, JsonOptions);

            if (result?.Recordings == null || result.Recordings.Count == 0)
            {
                _logger.LogDebug("No MusicBrainz recordings found for ISRC: {Isrc}", isrc);
                return null;
            }

            // Return the first recording (ISRCs should be unique)
            var recording = result.Recordings[0];
            var genres = recording.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string?>();
            _logger.LogInformation("✓ Found MusicBrainz recording for ISRC {Isrc}: {Title} by {Artist} (Genres: {Genres})",
                isrc, recording.Title, recording.ArtistCredit?[0]?.Name ?? "Unknown", string.Join(", ", genres));

            // Cache the result
            await _cache.SetAsync(cacheKey, recording, _cacheSettings.GenreTTL);

            return recording;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up ISRC {Isrc} in MusicBrainz", isrc);
            return null;
        }
    }

    /// <summary>
    /// Searches for recordings by title and artist.
    /// Note: Search API doesn't return genres, only MBIDs. Use LookupByMbidAsync to get genres.
    /// </summary>
    public async Task<List<MusicBrainzRecording>> SearchRecordingsAsync(string title, string artist, int limit = 5)
    {
        if (!_settings.Enabled)
        {
            return new List<MusicBrainzRecording>();
        }

        // Check cache first
        var cacheKey = CacheKeyBuilder.BuildMusicBrainzSearchKey(title, artist, limit);
        var cached = await _cache.GetAsync<List<MusicBrainzRecording>>(cacheKey);
        if (cached != null)
        {
            _logger.LogDebug("MusicBrainz search cache hit: {Title} - {Artist}", title, artist);
            return cached;
        }

        await RateLimitAsync();

        try
        {
            // Build Lucene query
            var query = $"recording:\"{title}\" AND artist:\"{artist}\"";
            var encodedQuery = Uri.EscapeDataString(query);
            // Note: Search API doesn't support inc=genres, only returns basic info + MBIDs
            var url = $"{_settings.BaseUrl}/recording?query={encodedQuery}&fmt=json&limit={limit}";

            _logger.LogDebug("MusicBrainz search: {Url}", url);

            using var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz search failed: {StatusCode}", response.StatusCode);
                return new List<MusicBrainzRecording>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<MusicBrainzSearchResponse>(json, JsonOptions);

            if (result?.Recordings == null || result.Recordings.Count == 0)
            {
                _logger.LogDebug("No MusicBrainz recordings found for: {Title} - {Artist}", title, artist);
                return new List<MusicBrainzRecording>();
            }

            _logger.LogDebug("Found {Count} MusicBrainz recordings for: {Title} - {Artist}",
                result.Recordings.Count, title, artist);

            // Cache the result
            await _cache.SetAsync(cacheKey, result.Recordings, _cacheSettings.GenreTTL);

            return result.Recordings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching MusicBrainz for: {Title} - {Artist}", title, artist);
            return new List<MusicBrainzRecording>();
        }
    }

    /// <summary>
    /// Looks up a recording by MBID to get full details including genres.
    /// </summary>
    public async Task<MusicBrainzRecording?> LookupByMbidAsync(string mbid)
    {
        if (!_settings.Enabled)
        {
            return null;
        }

        // Check cache first
        var cacheKey = CacheKeyBuilder.BuildMusicBrainzMbidKey(mbid);
        var cached = await _cache.GetAsync<MusicBrainzRecording>(cacheKey);
        if (cached != null)
        {
            _logger.LogDebug("MusicBrainz MBID cache hit: {Mbid}", mbid);
            return cached;
        }

        await RateLimitAsync();

        try
        {
            var url = $"{_settings.BaseUrl}/recording/{mbid}?fmt=json&inc=artists+releases+release-groups+genres+tags";
            _logger.LogDebug("MusicBrainz MBID lookup: {Url}", url);

            using var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz MBID lookup failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var recording = JsonSerializer.Deserialize<MusicBrainzRecording>(json, JsonOptions);

            if (recording == null)
            {
                _logger.LogDebug("No MusicBrainz recording found for MBID: {Mbid}", mbid);
                return null;
            }

            var genres = recording.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string?>();
            _logger.LogInformation("✓ Found MusicBrainz recording for MBID {Mbid}: {Title} by {Artist} (Genres: {Genres})",
                mbid, recording.Title, recording.ArtistCredit?[0]?.Name ?? "Unknown", string.Join(", ", genres));

            // Cache the result
            await _cache.SetAsync(cacheKey, recording, _cacheSettings.GenreTTL);

            return recording;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up MBID {Mbid} in MusicBrainz", mbid);
            return null;
        }
    }

    /// <summary>
    /// Enriches a song with genre information from MusicBrainz.
    /// First tries ISRC lookup, then falls back to title/artist search + MBID lookup.
    /// </summary>
    public async Task<List<string>> GetGenresForSongAsync(string title, string artist, string? isrc = null)
    {
        if (!_settings.Enabled)
        {
            return new List<string>();
        }

        MusicBrainzRecording? recording = null;

        // Try ISRC lookup first (most accurate and includes genres)
        if (!string.IsNullOrEmpty(isrc))
        {
            recording = await LookupByIsrcAsync(isrc);
        }

        // Fall back to search + MBID lookup if ISRC lookup failed or no ISRC provided
        if (recording == null)
        {
            var recordings = await SearchRecordingsAsync(title, artist, limit: 1);
            var searchResult = recordings.FirstOrDefault();

            // If we found a recording from search, do a full lookup by MBID to get genres
            if (searchResult != null && !string.IsNullOrEmpty(searchResult.Id))
            {
                recording = await LookupByMbidAsync(searchResult.Id);
            }
        }

        if (recording == null)
        {
            return new List<string>();
        }

        // Extract genres (prioritize official genres over tags)
        var genres = new List<string>();

        if (recording.Genres != null && recording.Genres.Count > 0)
        {
            // Get top genres by vote count
            genres.AddRange(recording.Genres
                .OrderByDescending(g => g.Count)
                .Take(5)
                .Select(g => g.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList());
        }

        _logger.LogDebug("Found {Count} genres for {Title} - {Artist}: {Genres}",
            genres.Count, title, artist, string.Join(", ", genres));

        return genres;
    }

    /// <summary>
    /// Rate limiting to comply with MusicBrainz API rules (1 request per second).
    /// </summary>
    private async Task RateLimitAsync()
    {
        await _rateLimitSemaphore.WaitAsync();
        try
        {
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            var minInterval = TimeSpan.FromMilliseconds(_settings.RateLimitMs);

            if (timeSinceLastRequest < minInterval)
            {
                var delay = minInterval - timeSinceLastRequest;
                await Task.Delay(delay);
            }

            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// MusicBrainz ISRC lookup response.
/// </summary>
public class MusicBrainzIsrcResponse
{
    [JsonPropertyName("recordings")]
    public List<MusicBrainzRecording>? Recordings { get; set; }
}

/// <summary>
/// MusicBrainz search response.
/// </summary>
public class MusicBrainzSearchResponse
{
    [JsonPropertyName("recordings")]
    public List<MusicBrainzRecording>? Recordings { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// MusicBrainz recording.
/// </summary>
public class MusicBrainzRecording
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("length")]
    public int? Length { get; set; } // in milliseconds

    [JsonPropertyName("artist-credit")]
    public List<MusicBrainzArtistCredit>? ArtistCredit { get; set; }

    [JsonPropertyName("releases")]
    public List<MusicBrainzRelease>? Releases { get; set; }

    [JsonPropertyName("isrcs")]
    public List<string>? Isrcs { get; set; }

    [JsonPropertyName("genres")]
    public List<MusicBrainzGenre>? Genres { get; set; }

    [JsonPropertyName("tags")]
    public List<MusicBrainzTag>? Tags { get; set; }
}

/// <summary>
/// MusicBrainz artist credit.
/// </summary>
public class MusicBrainzArtistCredit
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("artist")]
    public MusicBrainzArtist? Artist { get; set; }
}

/// <summary>
/// MusicBrainz artist.
/// </summary>
public class MusicBrainzArtist
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// MusicBrainz release.
/// </summary>
public class MusicBrainzRelease
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }
}

/// <summary>
/// MusicBrainz genre.
/// </summary>
public class MusicBrainzGenre
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// MusicBrainz tag (folksonomy).
/// </summary>
public class MusicBrainzTag
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
