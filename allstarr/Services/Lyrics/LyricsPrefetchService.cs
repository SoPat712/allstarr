using System.Text.Json;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Spotify;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Lyrics;

/// <summary>
/// Background service that prefetches lyrics for all tracks in injected Spotify playlists.
/// Lyrics are cached through the shared bounded application cache.
/// </summary>
public class LyricsPrefetchService : BackgroundService
{
    private readonly SpotifyImportSettings _spotifySettings;
    private readonly LrclibService _lrclibService;
    private readonly SpotifyPlaylistFetcher _playlistFetcher;
    private readonly IApplicationCache _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LyricsPrefetchService> _logger;
    private const int DelayBetweenRequestsMs = 500; // 500ms = 2 requests/second to be respectful

    public LyricsPrefetchService(
        IOptions<SpotifyImportSettings> spotifySettings,
        LrclibService lrclibService,
        SpotifyPlaylistFetcher playlistFetcher,
        IApplicationCache cache,
        IServiceProvider serviceProvider,
        ILogger<LyricsPrefetchService> logger)
    {
        _spotifySettings = spotifySettings.Value;
        _lrclibService = lrclibService;
        _playlistFetcher = playlistFetcher;
        _cache = cache;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LyricsPrefetchService: Starting up...");

        if (!_spotifySettings.Enabled)
        {
            _logger.LogInformation("Spotify playlist injection is DISABLED, lyrics prefetch will not run");
            return;
        }

        // Wait for playlist fetcher to initialize
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        // Run initial prefetch
        try
        {
            _logger.LogDebug("Running initial lyrics prefetch on startup");
            await PrefetchAllPlaylistLyricsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup lyrics prefetch");
        }

        // Run periodic prefetch (daily)
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);

            try
            {
                await PrefetchAllPlaylistLyricsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in lyrics prefetch service");
            }
        }
    }

    private async Task PrefetchAllPlaylistLyricsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🎵 Starting lyrics prefetch for {Count} playlists", _spotifySettings.Playlists.Count);

        var totalFetched = 0;
        var totalCached = 0;
        var totalMissing = 0;

        foreach (var playlist in _spotifySettings.Playlists)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var (fetched, cached, missing) = await PrefetchPlaylistLyricsAsync(playlist.Name, cancellationToken);
                totalFetched += fetched;
                totalCached += cached;
                totalMissing += missing;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error prefetching lyrics for playlist {Playlist}", playlist.Name);
            }
        }

        _logger.LogInformation("✅ Lyrics prefetch complete: {Fetched} fetched, {Cached} already cached, {Missing} not found",
            totalFetched, totalCached, totalMissing);
    }

    public async Task<(int Fetched, int Cached, int Missing)> PrefetchPlaylistLyricsAsync(
        string playlistName,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Prefetching lyrics for playlist: {Playlist}", playlistName);

        var tracks = await _playlistFetcher.GetPlaylistTracksAsync(playlistName);
        if (tracks.Count == 0)
        {
            _logger.LogWarning("No tracks found for playlist {Playlist}", playlistName);
            return (0, 0, 0);
        }

        // Get the pre-built playlist items cache which includes Jellyfin item IDs for local tracks
        var playlistItemsKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName);
        var playlistItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(playlistItemsKey);

        // Build a map of Spotify ID -> Jellyfin Item ID for quick lookup
        var spotifyToJellyfinId = new Dictionary<string, string>();
        if (playlistItems != null)
        {
            foreach (var item in playlistItems)
            {
                // Check if this is a local Jellyfin track (has Id field, no ProviderIds for external)
                if (item.TryGetValue("Id", out var idObj) && idObj != null)
                {
                    var jellyfinId = idObj.ToString();

                    // Try to get Spotify provider ID
                    if (item.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj != null)
                    {
                        var providerIdsJson = JsonSerializer.Serialize(providerIdsObj);
                        using var doc = JsonDocument.Parse(providerIdsJson);
                        if (doc.RootElement.TryGetProperty("Spotify", out var spotifyIdEl))
                        {
                            var spotifyId = spotifyIdEl.GetString();
                            if (!string.IsNullOrEmpty(spotifyId) && !string.IsNullOrEmpty(jellyfinId))
                            {
                                spotifyToJellyfinId[spotifyId] = jellyfinId;
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("Found {Count} local Jellyfin tracks with Spotify IDs in playlist {Playlist}",
                spotifyToJellyfinId.Count, playlistName);
        }

        var fetched = 0;
        var cached = 0;
        var missing = 0;

        foreach (var track in tracks)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                // Check if lyrics are already cached
                // Use same cache key format as LrclibService: join all artists with ", "
                var artistName = string.Join(", ", track.Artists);
                var cacheKey = CacheKeyBuilder.BuildLyricsKey(
                    artistName,
                    track.Title,
                    track.Album,
                    track.DurationMs / 1000);
                var existingLyrics = await _cache.GetStringAsync(cacheKey);

                if (!string.IsNullOrEmpty(existingLyrics))
                {
                    cached++;
                    _logger.LogInformation("✓ Lyrics already cached for {Artist} - {Track}", track.PrimaryArtist, track.Title);
                    continue;
                }

                // Priority 1: Check if this track has local Jellyfin lyrics (embedded in file)
                // Use the Jellyfin item ID from the playlist cache if available
                if (spotifyToJellyfinId.TryGetValue(track.SpotifyId, out var jellyfinItemId))
                {
                    var hasLocalLyrics = await CheckForLocalJellyfinLyricsByIdAsync(jellyfinItemId, track.PrimaryArtist, track.Title);
                    if (hasLocalLyrics)
                    {
                        cached++;
                        _logger.LogWarning("✓ Local Jellyfin lyrics found for {Artist} - {Track}, skipping external fetch",
                            track.PrimaryArtist, track.Title);

                        // Remove any previously cached LRCLib lyrics for this track
                        var artistNameForRemoval = string.Join(", ", track.Artists);
                        await RemoveCachedLyricsAsync(artistNameForRemoval, track.Title, track.Album, track.DurationMs / 1000);
                        continue;
                    }
                }

                // Priority 2: Try Spotify lyrics if we have a Spotify ID
                LyricsInfo? lyrics = null;
                if (!string.IsNullOrEmpty(track.SpotifyId))
                {
                    lyrics = await TryGetSpotifyLyricsAsync(track.SpotifyId, track.Title, track.PrimaryArtist);
                }

                // Priority 3: Fall back to LRCLib if no Spotify lyrics
                if (lyrics == null)
                {
                    lyrics = await _lrclibService.GetLyricsAsync(
                        track.Title,
                        track.Artists.ToArray(),
                        track.Album,
                        track.DurationMs / 1000);
                }

                if (lyrics != null)
                {
                    fetched++;
                    _logger.LogInformation("✓ Fetched lyrics for {Artist} - {Track} (synced: {HasSynced})",
                        track.PrimaryArtist, track.Title, !string.IsNullOrEmpty(lyrics.SyncedLyrics));

                }
                else
                {
                    missing++;
                    _logger.LogDebug("✗ No lyrics found for {Artist} - {Track}", track.PrimaryArtist, track.Title);
                }

                // Rate limiting
                await Task.Delay(DelayBetweenRequestsMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prefetch lyrics for {Artist} - {Track}", track.PrimaryArtist, track.Title);
                missing++;
            }
        }

        _logger.LogDebug("Playlist {Playlist}: {Fetched} fetched, {Cached} cached, {Missing} missing",
            playlistName, fetched, cached, missing);

        return (fetched, cached, missing);
    }

    /// <summary>
    /// Removes cached LRCLib lyrics from the shared application cache.
    /// Used when a track has local Jellyfin lyrics, making the LRCLib cache obsolete.
    /// </summary>
    private async Task RemoveCachedLyricsAsync(string artist, string title, string album, int duration)
    {
        try
        {
            var cacheKey = CacheKeyBuilder.BuildLyricsKey(artist, title, album, duration);
            await _cache.DeleteAsync(cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove cached lyrics for {Artist} - {Track}", artist, title);
        }
    }

    /// <summary>
    /// Tries to get lyrics from Spotify using the track's Spotify ID.
    /// Returns null if Spotify API is not enabled or lyrics not found.
    /// </summary>
    private async Task<LyricsInfo?> TryGetSpotifyLyricsAsync(string spotifyTrackId, string trackTitle, string artistName)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var spotifyLyricsService = scope.ServiceProvider.GetService<SpotifyLyricsService>();

            if (spotifyLyricsService == null)
            {
                return null;
            }

            var spotifyLyrics = await spotifyLyricsService.GetLyricsByTrackIdAsync(spotifyTrackId);

            if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
            {
                _logger.LogDebug("✓ Found Spotify lyrics for {Artist} - {Track} ({LineCount} lines)",
                    artistName, trackTitle, spotifyLyrics.Lines.Count);
                return spotifyLyricsService.ToLyricsInfo(spotifyLyrics);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Spotify lyrics for track {SpotifyId}", spotifyTrackId);
            return null;
        }
    }

    /// <summary>
    /// Checks if a track has embedded lyrics in Jellyfin using the Jellyfin item ID.
    /// This is the most efficient method as it directly queries the lyrics endpoint.
    /// </summary>
    private async Task<bool> CheckForLocalJellyfinLyricsByIdAsync(string jellyfinItemId, string artistName, string trackTitle)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var proxyService = scope.ServiceProvider.GetService<JellyfinProxyService>();

            if (proxyService == null)
            {
                return false;
            }

            // Directly check if this track has lyrics using the item ID
            // Use internal method with server API key since this is a background operation
            var (lyricsResult, lyricsStatusCode) = await proxyService.GetJsonAsyncInternal(
                $"Audio/{jellyfinItemId}/Lyrics",
                null);

            if (lyricsResult != null && lyricsStatusCode == 200)
            {
                // Track has embedded lyrics in Jellyfin
                _logger.LogDebug("Found embedded lyrics in Jellyfin for {Artist} - {Track} (ID: {JellyfinId})",
                    artistName, trackTitle, jellyfinItemId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Jellyfin lyrics for item {ItemId}", jellyfinItemId);
            return false;
        }
    }

    /// <summary>
    /// Checks if a track has embedded lyrics in Jellyfin by querying the Jellyfin API.
    /// This prevents downloading lyrics from LRCLib when the local file already has them.
    /// </summary>
    private async Task<bool> CheckForLocalJellyfinLyricsAsync(string spotifyTrackId, string artistName, string trackTitle)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var proxyService = scope.ServiceProvider.GetService<JellyfinProxyService>();

            if (proxyService == null)
            {
                return false;
            }

            // Search for the track in Jellyfin by artist and title
            // Jellyfin doesn't support anyProviderIdEquals - that's an Emby API parameter
            var searchTerm = $"{artistName} {trackTitle}";
            var searchParams = new Dictionary<string, string>
            {
                ["searchTerm"] = searchTerm,
                ["includeItemTypes"] = "Audio",
                ["recursive"] = "true",
                ["limit"] = "5"  // Get a few results to find best match
            };

            var (searchResult, statusCode) = await proxyService.GetJsonAsyncInternal("Items", searchParams);

            if (searchResult == null || statusCode != 200)
            {
                // Track not found in local library
                return false;
            }

            // Check if we found any items
            if (!searchResult.RootElement.TryGetProperty("Items", out var items) ||
                items.GetArrayLength() == 0)
            {
                return false;
            }

            // Find the best matching track by comparing artist and title
            string? bestMatchId = null;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("Name", out var nameEl) ||
                    !item.TryGetProperty("Id", out var idEl))
                {
                    continue;
                }

                var itemTitle = nameEl.GetString() ?? "";
                var itemId = idEl.GetString();

                // Check if title matches (case-insensitive)
                if (itemTitle.Equals(trackTitle, StringComparison.OrdinalIgnoreCase))
                {
                    // Also check artist if available
                    if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                    {
                        var itemArtist = artistsEl[0].GetString() ?? "";
                        if (itemArtist.Equals(artistName, StringComparison.OrdinalIgnoreCase))
                        {
                            bestMatchId = itemId;
                            break;  // Exact match found
                        }
                    }

                    // If no exact artist match but title matches, use it as fallback
                    if (bestMatchId == null)
                    {
                        bestMatchId = itemId;
                    }
                }
            }

            if (string.IsNullOrEmpty(bestMatchId))
            {
                return false;
            }

            // Check if this track has lyrics
            // Use internal method with server API key since this is a background operation
            var (lyricsResult, lyricsStatusCode) = await proxyService.GetJsonAsyncInternal(
                $"Audio/{bestMatchId}/Lyrics",
                null);

            if (lyricsResult != null && lyricsStatusCode == 200)
            {
                // Track has embedded lyrics in Jellyfin
                _logger.LogDebug("Found embedded lyrics in Jellyfin for {Artist} - {Track} (Jellyfin ID: {JellyfinId})",
                    artistName, trackTitle, bestMatchId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for local Jellyfin lyrics for Spotify track {SpotifyId}", spotifyTrackId);
            return false;
        }
    }
}
