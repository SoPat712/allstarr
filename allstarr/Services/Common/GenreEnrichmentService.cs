using allstarr.Models.Domain;
using allstarr.Services.MusicBrainz;

namespace allstarr.Services.Common;

/// <summary>
/// Service for enriching songs and playlists with genre information from MusicBrainz.
/// </summary>
public class GenreEnrichmentService
{
    private readonly MusicBrainzService _musicBrainz;
    private readonly IApplicationCache _cache;
    private readonly ILogger<GenreEnrichmentService> _logger;
    private static readonly TimeSpan GenreCacheDuration = TimeSpan.FromDays(30);

    public GenreEnrichmentService(
        MusicBrainzService musicBrainz,
        IApplicationCache cache,
        ILogger<GenreEnrichmentService> logger)
    {
        _musicBrainz = musicBrainz;
        _cache = cache;
        _logger = logger;
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

        var cacheEntryKey = CacheKeyBuilder.BuildGenreEnrichmentKey(cacheKey);
        var cachedGenre = await _cache.GetAsync<string>(cacheEntryKey);

        if (cachedGenre != null)
        {
            if (cachedGenre.Length > 0)
            {
                song.Genre = cachedGenre;
            }
            _logger.LogDebug("Using cached genre for {Title} - {Artist}: {Genre}",
                song.Title, song.Artist, cachedGenre.Length > 0 ? cachedGenre : "(none)");
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

                await _cache.SetAsync(cacheEntryKey, song.Genre, GenreCacheDuration);

                _logger.LogInformation("Enriched {Title} - {Artist} with genre: {Genre}",
                    song.Title, song.Artist, song.Genre);
            }
            else
            {
                await _cache.SetAsync(cacheEntryKey, string.Empty, GenreCacheDuration);
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
    /// Returns all unique genres from the songs.
    /// </summary>
    public List<string> AggregatePlaylistGenres(List<Song> songs)
    {
        return songs
            .Where(s => !string.IsNullOrEmpty(s.Genre))
            .Select(s => s.Genre!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}
