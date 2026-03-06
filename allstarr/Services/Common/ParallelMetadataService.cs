using allstarr.Models.Domain;
using allstarr.Models.Search;

namespace allstarr.Services.Common;

/// <summary>
/// Races multiple metadata providers in parallel and returns the fastest result.
/// Used for search operations to minimize latency.
/// </summary>
public class ParallelMetadataService
{
    private readonly IEnumerable<IMusicMetadataService> _providers;
    private readonly ILogger<ParallelMetadataService> _logger;

    public ParallelMetadataService(
        IEnumerable<IMusicMetadataService> providers,
        ILogger<ParallelMetadataService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// Races all providers and returns the first successful result.
    /// Falls back to next provider if first one fails.
    /// </summary>
    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        if (!_providers.Any())
        {
            _logger.LogWarning("No metadata providers available for parallel search");
            return new SearchResult();
        }

        _logger.LogDebug("🏁 Racing {Count} providers for search: {Query}", _providers.Count(), query);

        // Create tasks for all providers
        var tasks = _providers.Select(async provider =>
        {
            var providerName = provider.GetType().Name;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await provider.SearchAllAsync(query, songLimit, albumLimit, artistLimit, cancellationToken);
                sw.Stop();
                
                _logger.LogInformation("✅ {Provider} completed search in {Ms}ms ({Songs} songs, {Albums} albums, {Artists} artists)",
                    providerName, sw.ElapsedMilliseconds, result.Songs.Count, result.Albums.Count, result.Artists.Count);
                
                return (Success: true, Result: result, Provider: providerName, ElapsedMs: sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ {Provider} search failed", providerName);
                return (Success: false, Result: new SearchResult(), Provider: providerName, ElapsedMs: 0L);
            }
        }).ToList();

        // Wait for first successful result
        while (tasks.Any())
        {
            var completedTask = await Task.WhenAny(tasks);
            var result = await completedTask;

            if (result.Success && (result.Result.Songs.Any() || result.Result.Albums.Any() || result.Result.Artists.Any()))
            {
                _logger.LogDebug("🏆 Using results from {Provider} ({Ms}ms) - fastest with results",
                    result.Provider, result.ElapsedMs);
                return result.Result;
            }

            // Remove completed task and try next
            tasks.Remove(completedTask);
        }

        // All providers failed or returned empty
        _logger.LogWarning("⚠️ All providers failed or returned empty results for: {Query}", query);
        return new SearchResult();
    }

    /// <summary>
    /// Searches for a specific song by title and artist across all providers in parallel.
    /// Returns the first successful match.
    /// </summary>
    public async Task<Song?> SearchSongAsync(string title, string artist, int limit = 5, CancellationToken cancellationToken = default)
    {
        if (!_providers.Any())
        {
            return null;
        }

        _logger.LogDebug("🏁 Racing {Count} providers for song: {Title} - {Artist}", _providers.Count(), title, artist);

        var tasks = _providers.Select(async provider =>
        {
            var providerName = provider.GetType().Name;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var songs = await provider.SearchSongsAsync($"{title} {artist}", limit, cancellationToken);
                sw.Stop();
                
                var bestMatch = songs.FirstOrDefault();
                if (bestMatch != null)
                {
                    _logger.LogInformation("✅ {Provider} found song in {Ms}ms", providerName, sw.ElapsedMilliseconds);
                }
                
                return (Success: bestMatch != null, Song: bestMatch, Provider: providerName, ElapsedMs: sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ {Provider} song search failed", providerName);
                return (Success: false, Song: (Song?)null, Provider: providerName, ElapsedMs: 0L);
            }
        }).ToList();

        // Wait for first successful result
        while (tasks.Any())
        {
            var completedTask = await Task.WhenAny(tasks);
            var result = await completedTask;

            if (result.Success && result.Song != null)
            {
                _logger.LogInformation("🏆 Using song from {Provider} ({Ms}ms)", result.Provider, result.ElapsedMs);
                return result.Song;
            }

            tasks.Remove(completedTask);
        }

        return null;
    }
}
