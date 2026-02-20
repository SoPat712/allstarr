<<<<<<< HEAD
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Lyrics;

/// <summary>
/// Orchestrates lyrics fetching from multiple sources with priority-based fallback.
/// Priority order: Spotify → LyricsPlus → LRCLib
/// Note: Jellyfin local lyrics are handled by the controller before calling this orchestrator.
/// </summary>
public class LyricsOrchestrator
{
    private readonly SpotifyLyricsService _spotifyLyrics;
    private readonly LyricsPlusService _lyricsPlus;
    private readonly LrclibService _lrclib;
    private readonly SpotifyApiSettings _spotifySettings;
    private readonly ILogger<LyricsOrchestrator> _logger;

    public LyricsOrchestrator(
        SpotifyLyricsService spotifyLyrics,
        LyricsPlusService lyricsPlus,
        LrclibService lrclib,
        IOptions<SpotifyApiSettings> spotifySettings,
        ILogger<LyricsOrchestrator> logger)
    {
        _spotifyLyrics = spotifyLyrics;
        _lyricsPlus = lyricsPlus;
        _lrclib = lrclib;
        _spotifySettings = spotifySettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Fetches lyrics with automatic fallback through all available sources.
    /// Note: Jellyfin local lyrics are handled by the controller before calling this.
    /// </summary>
    /// <param name="trackName">Track title</param>
    /// <param name="artistNames">Artist names (can be multiple)</param>
    /// <param name="albumName">Album name</param>
    /// <param name="durationSeconds">Track duration in seconds</param>
    /// <param name="spotifyTrackId">Spotify track ID (if available)</param>
    /// <returns>Lyrics info or null if not found</returns>
    public async Task<LyricsInfo?> GetLyricsAsync(
        string trackName,
        string[] artistNames,
        string? albumName,
        int durationSeconds,
        string? spotifyTrackId = null)
    {
        var artistName = string.Join(", ", artistNames);
        
        _logger.LogInformation("🎵 Fetching lyrics for: {Artist} - {Track}", artistName, trackName);

        // 1. Try Spotify lyrics (if Spotify ID provided)
        if (!string.IsNullOrEmpty(spotifyTrackId))
        {
            var spotifyLyrics = await TrySpotifyLyrics(spotifyTrackId, artistName, trackName);
            if (spotifyLyrics != null)
            {
                return spotifyLyrics;
            }
        }

        // 2. Try LyricsPlus
        var lyricsPlusLyrics = await TryLyricsPlusLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lyricsPlusLyrics != null)
        {
            return lyricsPlusLyrics;
        }

        // 3. Try LRCLib
        var lrclibLyrics = await TryLrclibLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lrclibLyrics != null)
        {
            return lrclibLyrics;
        }

        _logger.LogInformation("❌ No lyrics found for: {Artist} - {Track}", artistName, trackName);
        return null;
    }

    /// <summary>
    /// Prefetches lyrics in the background (for cache warming).
    /// Skips Jellyfin local since we don't have an itemId.
    /// </summary>
    public async Task<bool> PrefetchLyricsAsync(
        string trackName,
        string[] artistNames,
        string? albumName,
        int durationSeconds,
        string? spotifyTrackId = null)
    {
        var artistName = string.Join(", ", artistNames);
        
        _logger.LogDebug("🎵 Prefetching lyrics for: {Artist} - {Track}", artistName, trackName);

        // 1. Try Spotify lyrics (if Spotify ID provided)
        if (!string.IsNullOrEmpty(spotifyTrackId))
        {
            var spotifyLyrics = await TrySpotifyLyrics(spotifyTrackId, artistName, trackName);
            if (spotifyLyrics != null)
            {
                return true;
            }
        }

        // 2. Try LyricsPlus
        var lyricsPlusLyrics = await TryLyricsPlusLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lyricsPlusLyrics != null)
        {
            return true;
        }

        // 3. Try LRCLib
        var lrclibLyrics = await TryLrclibLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lrclibLyrics != null)
        {
            return true;
        }

        _logger.LogDebug("No lyrics found for prefetch: {Artist} - {Track}", artistName, trackName);
        return false;
    }

    #region Private Helper Methods

    private async Task<LyricsInfo?> TrySpotifyLyrics(string spotifyTrackId, string artistName, string trackName)
    {
        if (!_spotifySettings.Enabled)
        {
            _logger.LogWarning("Spotify API not enabled, skipping Spotify lyrics");
            return null;
        }

        try
        {
            // Validate Spotify ID format
            var cleanSpotifyId = spotifyTrackId.Replace("spotify:track:", "").Trim();
            
            if (cleanSpotifyId.Length != 22 || cleanSpotifyId.Contains(":") || cleanSpotifyId.Contains("local"))
            {
                _logger.LogWarning("Invalid Spotify ID format: {SpotifyId}, skipping", spotifyTrackId);
                return null;
            }

            _logger.LogDebug("→ Trying Spotify lyrics for track ID: {SpotifyId}", cleanSpotifyId);
            
            var spotifyLyrics = await _spotifyLyrics.GetLyricsByTrackIdAsync(cleanSpotifyId);
            
            if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
            {
                _logger.LogDebug("✓ Found Spotify lyrics for {Artist} - {Track} ({LineCount} lines, type: {SyncType})", 
                    artistName, trackName, spotifyLyrics.Lines.Count, spotifyLyrics.SyncType);
                
                return _spotifyLyrics.ToLyricsInfo(spotifyLyrics);
            }
            
            _logger.LogDebug("No Spotify lyrics found for track ID {SpotifyId}", cleanSpotifyId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Spotify lyrics for track ID {SpotifyId}", spotifyTrackId);
            return null;
        }
    }

    private async Task<LyricsInfo?> TryLyricsPlusLyrics(
        string trackName, 
        string[] artistNames, 
        string? albumName, 
        int durationSeconds,
        string artistName)
    {
        try
        {
            _logger.LogDebug("→ Trying LyricsPlus for: {Artist} - {Track}", artistName, trackName);
            
            var lyrics = await _lyricsPlus.GetLyricsAsync(trackName, artistNames, albumName, durationSeconds);
            
            if (lyrics != null)
            {
                _logger.LogInformation("✓ Found LyricsPlus lyrics for {Artist} - {Track}", artistName, trackName);
                return lyrics;
            }
            
            _logger.LogDebug("No LyricsPlus lyrics found for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching LyricsPlus lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    private async Task<LyricsInfo?> TryLrclibLyrics(
        string trackName, 
        string[] artistNames, 
        string? albumName, 
        int durationSeconds,
        string artistName)
    {
        try
        {
            _logger.LogDebug("→ Trying LRCLib for: {Artist} - {Track}", artistName, trackName);
            
            var lyrics = await _lrclib.GetLyricsAsync(trackName, artistNames, albumName ?? string.Empty, durationSeconds);
            
            if (lyrics != null)
            {
                _logger.LogInformation("✓ Found LRCLib lyrics for {Artist} - {Track}", artistName, trackName);
                return lyrics;
            }
            
            _logger.LogDebug("No LRCLib lyrics found for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching LRCLib lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    #endregion
}
||||||| f68706f
=======
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Lyrics;

/// <summary>
/// Orchestrates lyrics fetching from multiple sources with priority-based fallback.
/// Priority order: Spotify → LyricsPlus → LRCLib
/// Note: Jellyfin local lyrics are handled by the controller before calling this orchestrator.
/// </summary>
public class LyricsOrchestrator
{
    private readonly SpotifyLyricsService _spotifyLyrics;
    private readonly LyricsPlusService _lyricsPlus;
    private readonly LrclibService _lrclib;
    private readonly SpotifyApiSettings _spotifySettings;
    private readonly ILogger<LyricsOrchestrator> _logger;

    public LyricsOrchestrator(
        SpotifyLyricsService spotifyLyrics,
        LyricsPlusService lyricsPlus,
        LrclibService lrclib,
        IOptions<SpotifyApiSettings> spotifySettings,
        ILogger<LyricsOrchestrator> logger)
    {
        _spotifyLyrics = spotifyLyrics;
        _lyricsPlus = lyricsPlus;
        _lrclib = lrclib;
        _spotifySettings = spotifySettings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Fetches lyrics with automatic fallback through all available sources.
    /// Note: Jellyfin local lyrics are handled by the controller before calling this.
    /// </summary>
    /// <param name="trackName">Track title</param>
    /// <param name="artistNames">Artist names (can be multiple)</param>
    /// <param name="albumName">Album name</param>
    /// <param name="durationSeconds">Track duration in seconds</param>
    /// <param name="spotifyTrackId">Spotify track ID (if available)</param>
    /// <returns>Lyrics info or null if not found</returns>
    public async Task<LyricsInfo?> GetLyricsAsync(
        string trackName,
        string[] artistNames,
        string? albumName,
        int durationSeconds,
        string? spotifyTrackId = null)
    {
        var artistName = string.Join(", ", artistNames);
        
        _logger.LogInformation("🎵 Fetching lyrics for: {Artist} - {Track}", artistName, trackName);

        // 1. Try Spotify lyrics (if Spotify ID provided)
        if (!string.IsNullOrEmpty(spotifyTrackId))
        {
            var spotifyLyrics = await TrySpotifyLyrics(spotifyTrackId, artistName, trackName);
            if (spotifyLyrics != null)
            {
                return spotifyLyrics;
            }
        }

        // 2. Try LyricsPlus
        var lyricsPlusLyrics = await TryLyricsPlusLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lyricsPlusLyrics != null)
        {
            return lyricsPlusLyrics;
        }

        // 3. Try LRCLib
        var lrclibLyrics = await TryLrclibLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lrclibLyrics != null)
        {
            return lrclibLyrics;
        }

        _logger.LogInformation("❌ No lyrics found for: {Artist} - {Track}", artistName, trackName);
        return null;
    }

    /// <summary>
    /// Prefetches lyrics in the background (for cache warming).
    /// Skips Jellyfin local since we don't have an itemId.
    /// </summary>
    public async Task<bool> PrefetchLyricsAsync(
        string trackName,
        string[] artistNames,
        string? albumName,
        int durationSeconds,
        string? spotifyTrackId = null)
    {
        var artistName = string.Join(", ", artistNames);
        
        _logger.LogDebug("🎵 Prefetching lyrics for: {Artist} - {Track} (Spotify ID: {SpotifyId})", 
            artistName, trackName, spotifyTrackId ?? "none");

        // 1. Try Spotify lyrics (if Spotify ID provided)
        if (!string.IsNullOrEmpty(spotifyTrackId))
        {
            var spotifyLyrics = await TrySpotifyLyrics(spotifyTrackId, artistName, trackName);
            if (spotifyLyrics != null)
            {
                return true;
            }
        }
        else
        {
            _logger.LogDebug("No Spotify ID available for prefetch, skipping Spotify lyrics");
        }

        // 2. Try LyricsPlus
        var lyricsPlusLyrics = await TryLyricsPlusLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lyricsPlusLyrics != null)
        {
            return true;
        }

        // 3. Try LRCLib
        var lrclibLyrics = await TryLrclibLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
        if (lrclibLyrics != null)
        {
            return true;
        }

        _logger.LogDebug("No lyrics found for prefetch: {Artist} - {Track}", artistName, trackName);
        return false;
    }

    #region Private Helper Methods

    private async Task<LyricsInfo?> TrySpotifyLyrics(string spotifyTrackId, string artistName, string trackName)
    {
        if (!_spotifySettings.Enabled)
        {
            _logger.LogWarning("Spotify API not enabled, skipping Spotify lyrics");
            return null;
        }

        try
        {
            // Validate Spotify ID format
            var cleanSpotifyId = spotifyTrackId.Replace("spotify:track:", "").Trim();
            
            if (cleanSpotifyId.Length != 22 || cleanSpotifyId.Contains(":") || cleanSpotifyId.Contains("local"))
            {
                _logger.LogWarning("Invalid Spotify ID format: {SpotifyId}, skipping", spotifyTrackId);
                return null;
            }

            _logger.LogDebug("→ Trying Spotify lyrics for track ID: {SpotifyId}", cleanSpotifyId);
            
            var spotifyLyrics = await _spotifyLyrics.GetLyricsByTrackIdAsync(cleanSpotifyId);
            
            if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
            {
                _logger.LogDebug("✓ Found Spotify lyrics for {Artist} - {Track} ({LineCount} lines, type: {SyncType})", 
                    artistName, trackName, spotifyLyrics.Lines.Count, spotifyLyrics.SyncType);
                
                return _spotifyLyrics.ToLyricsInfo(spotifyLyrics);
            }
            
            _logger.LogDebug("No Spotify lyrics found for track ID {SpotifyId}", cleanSpotifyId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Spotify lyrics for track ID {SpotifyId}", spotifyTrackId);
            return null;
        }
    }

    private async Task<LyricsInfo?> TryLyricsPlusLyrics(
        string trackName, 
        string[] artistNames, 
        string? albumName, 
        int durationSeconds,
        string artistName)
    {
        try
        {
            _logger.LogDebug("→ Trying LyricsPlus for: {Artist} - {Track}", artistName, trackName);
            
            var lyrics = await _lyricsPlus.GetLyricsAsync(trackName, artistNames, albumName, durationSeconds);
            
            if (lyrics != null)
            {
                // LyricsPlus already logs with source info, so we just confirm success
                _logger.LogDebug("✓ LyricsOrchestrator: Using LyricsPlus lyrics for {Artist} - {Track}", artistName, trackName);
                return lyrics;
            }
            
            _logger.LogDebug("No LyricsPlus lyrics found for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching LyricsPlus lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    private async Task<LyricsInfo?> TryLrclibLyrics(
        string trackName, 
        string[] artistNames, 
        string? albumName, 
        int durationSeconds,
        string artistName)
    {
        try
        {
            _logger.LogDebug("→ Trying LRCLib for: {Artist} - {Track}", artistName, trackName);
            
            var lyrics = await _lrclib.GetLyricsAsync(trackName, artistNames, albumName ?? string.Empty, durationSeconds);
            
            if (lyrics != null)
            {
                _logger.LogInformation("✓ LyricsOrchestrator: Using LRCLib lyrics for {Artist} - {Track}", artistName, trackName);
                return lyrics;
            }
            
            _logger.LogDebug("No LRCLib lyrics found for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching LRCLib lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    #endregion
}
>>>>>>> beta
