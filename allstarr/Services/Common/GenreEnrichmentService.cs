using allstarr.Models.Domain;
using allstarr.Services.MusicBrainz;
using allstarr.Services.Common;
using System.Text.Json;

namespace allstarr.Services.Common;

/// <summary>
/// Service for enriching songs and playlists with genre information from MusicBrainz.
/// </summary>
public class GenreEnrichmentService
{
    private readonly MusicBrainzService _musicBrainz;
    private readonly RedisCacheService _cache;
    private readonly ILogger<GenreEnrichmentService> _logger;
    private const string GenreCachePrefix = "genre:";
    private const string GenreCacheDirectory = "/app/cache/genres";
    private static readonly TimeSpan GenreCacheDuration = TimeSpan.FromDays(30);

    public GenreEnrichmentService(
        MusicBrainzService musicBrainz,
        RedisCacheService cache,
        ILogger<GenreEnrichmentService> logger)
    {
        _musicBrainz = musicBrainz;
        _cache = cache;
        _logger = logger;
        
        // Ensure cache directory exists
        Directory.CreateDirectory(GenreCacheDirectory);
    }

    /// <summary>
    /// Enriches a song with genre information from MusicBrainz (with caching).
    /// Updates the song's Genre property with the top genre.
    /// </summary>
    public async Task EnrichSongGenreAsync(Song song)
    {
        // Skip if song already has a genre
        if (!string.IsNullOrEmpty(song.Genre))
        {
            return;
        }

        var cacheKey = $"{song.Title}:{song.Artist}";
        
        // Check Redis cache first
        var redisCacheKey = $"{GenreCachePrefix}{cacheKey}";
        var cachedGenre = await _cache.GetAsync<string>(redisCacheKey);
        
        if (cachedGenre != null)
        {
            song.Genre = cachedGenre;
            _logger.LogDebug("Using Redis cached genre for {Title} - {Artist}: {Genre}", 
                song.Title, song.Artist, cachedGenre);
            return;
        }

        // Check file cache
        var fileCachedGenre = await GetFromFileCacheAsync(cacheKey);
        if (fileCachedGenre != null)
        {
            song.Genre = fileCachedGenre;
            // Restore to Redis cache
            await _cache.SetAsync(redisCacheKey, fileCachedGenre, GenreCacheDuration);
            _logger.LogDebug("Using file cached genre for {Title} - {Artist}: {Genre}", 
                song.Title, song.Artist, fileCachedGenre);
            return;
        }

        // Fetch from MusicBrainz
        try
        {
            var genres = await _musicBrainz.GetGenresForSongAsync(song.Title, song.Artist, song.Isrc);
            
            if (genres.Count > 0)
            {
                // Use the top genre
                song.Genre = genres[0];
                
                // Cache in both Redis and file
                await _cache.SetAsync(redisCacheKey, song.Genre, GenreCacheDuration);
                await SaveToFileCacheAsync(cacheKey, song.Genre);
                
                _logger.LogInformation("Enriched {Title} - {Artist} with genre: {Genre}", 
                    song.Title, song.Artist, song.Genre);
            }
            else
            {
                // Cache negative result to avoid repeated lookups
                await SaveToFileCacheAsync(cacheKey, "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enrich genre for {Title} - {Artist}", 
                song.Title, song.Artist);
        }
    }

    /// <summary>
    /// Enriches multiple songs with genre information (batch operation).
    /// </summary>
    public async Task EnrichSongsGenresAsync(List<Song> songs)
    {
        var tasks = songs
            .Where(s => string.IsNullOrEmpty(s.Genre))
            .Select(s => EnrichSongGenreAsync(s));
        
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Aggregates genres from a list of songs to determine playlist genres.
    /// Returns the top 5 most common genres.
    /// </summary>
    public List<string> AggregatePlaylistGenres(List<Song> songs)
    {
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var song in songs)
        {
            if (!string.IsNullOrEmpty(song.Genre))
            {
                if (genreCounts.ContainsKey(song.Genre))
                {
                    genreCounts[song.Genre]++;
                }
                else
                {
                    genreCounts[song.Genre] = 1;
                }
            }
        }

        return genreCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(5)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Gets genre from file cache.
    /// </summary>
    private async Task<string?> GetFromFileCacheAsync(string cacheKey)
    {
        try
        {
            var fileName = GetCacheFileName(cacheKey);
            var filePath = Path.Combine(GenreCacheDirectory, fileName);

            if (!File.Exists(filePath))
            {
                return null;
            }

            // Check if cache is expired (30 days)
            var fileInfo = new FileInfo(filePath);
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > GenreCacheDuration)
            {
                File.Delete(filePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var cacheEntry = JsonSerializer.Deserialize<GenreCacheEntry>(json);
            
            return cacheEntry?.Genre;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read genre from file cache for {Key}", cacheKey);
            return null;
        }
    }

    /// <summary>
    /// Saves genre to file cache.
    /// </summary>
    private async Task SaveToFileCacheAsync(string cacheKey, string genre)
    {
        try
        {
            var fileName = GetCacheFileName(cacheKey);
            var filePath = Path.Combine(GenreCacheDirectory, fileName);

            var cacheEntry = new GenreCacheEntry
            {
                CacheKey = cacheKey,
                Genre = genre,
                CachedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(cacheEntry, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save genre to file cache for {Key}", cacheKey);
        }
    }

    /// <summary>
    /// Generates a safe file name from cache key.
    /// </summary>
    private static string GetCacheFileName(string cacheKey)
    {
        // Use base64 encoding to create safe file names
        var bytes = System.Text.Encoding.UTF8.GetBytes(cacheKey);
        var base64 = Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
        return $"{base64}.json";
    }

    private class GenreCacheEntry
    {
        public string CacheKey { get; set; } = "";
        public string Genre { get; set; } = "";
        public DateTime CachedAt { get; set; }
    }
}
