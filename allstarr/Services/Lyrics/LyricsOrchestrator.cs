using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using allstarr.Services.Common;

namespace allstarr.Services.Lyrics;

public class LyricsOrchestrator
{
    private readonly SpotifyLyricsService _spotifyLyrics;
    private readonly LrclibService _lrclib;
    private readonly SpotifyApiSettings _spotifySettings;
    private readonly ProviderStatusManager _statusManager;
    private readonly ILogger<LyricsOrchestrator> _logger;

    public LyricsOrchestrator(
        SpotifyLyricsService spotifyLyrics,
        LrclibService lrclib,
        IOptions<SpotifyApiSettings> spotifySettings,
        ProviderStatusManager statusManager,
        ILogger<LyricsOrchestrator> logger)
    {
        _spotifyLyrics = spotifyLyrics;
        _lrclib = lrclib;
        _spotifySettings = spotifySettings.Value;
        _statusManager = statusManager;
        _logger = logger;
    }

    public async Task<LyricsInfo?> GetLyricsAsync(
        string trackName,
        string[] artistNames,
        string? albumName,
        int durationSeconds,
        string? spotifyTrackId = null)
    {
        var artistName = string.Join(", ", artistNames);
        _logger.LogInformation("🎵 Fetching lyrics for: {Artist} - {Track}", artistName, trackName);

        var order = _statusManager.GetEnabledLyricsProviders();

        foreach (var source in order)
        {
            try
            {
                if (source.Equals("spotify", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(spotifyTrackId))
                    {
                        var spotifyLyrics = await TrySpotifyLyrics(spotifyTrackId, artistName, trackName);
                        if (spotifyLyrics != null) return spotifyLyrics;
                    }
                }
                else if (source.Equals("lrclib", StringComparison.OrdinalIgnoreCase))
                {
                    var lrclibLyrics = await TryLrclibLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
                    if (lrclibLyrics != null) return lrclibLyrics;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed fetching lyrics from source: {Source}", source);
            }
        }

        _logger.LogInformation("❌ No lyrics found for: {Artist} - {Track}", artistName, trackName);
        return null;
    }

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

        var order = _statusManager.GetEnabledLyricsProviders();

        foreach (var source in order)
        {
            try
            {
                if (source.Equals("spotify", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(spotifyTrackId))
                    {
                        var spotifyLyrics = await TrySpotifyLyrics(spotifyTrackId, artistName, trackName);
                        if (spotifyLyrics != null) return true;
                    }
                }
                else if (source.Equals("lrclib", StringComparison.OrdinalIgnoreCase))
                {
                    var lrclibLyrics = await TryLrclibLyrics(trackName, artistNames, albumName, durationSeconds, artistName);
                    if (lrclibLyrics != null) return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed prefetching lyrics from source: {Source}", source);
            }
        }

        _logger.LogDebug("No lyrics found for prefetch: {Artist} - {Track}", artistName, trackName);
        return false;
    }

    private async Task<LyricsInfo?> TrySpotifyLyrics(string spotifyTrackId, string artistName, string trackName)
    {
        if (string.IsNullOrWhiteSpace(_spotifySettings.LyricsApiUrl))
        {
            _logger.LogDebug("Spotify lyrics sidecar not configured, skipping Spotify lyrics");
            return null;
        }

        try
        {
            const string prefix = "spotify:track:";
            var value = spotifyTrackId.Trim();
            var cleanSpotifyId = value.StartsWith(prefix, StringComparison.Ordinal)
                ? value[prefix.Length..]
                : value;

            if (cleanSpotifyId.Length != 22 || cleanSpotifyId.Contains(":") || cleanSpotifyId.Contains("local"))
            {
                _logger.LogWarning("Invalid Spotify ID format: {SpotifyId}, skipping", spotifyTrackId);
                return null;
            }

            _logger.LogDebug("Trying Spotify lyrics for track ID: {SpotifyId}", cleanSpotifyId);

            var spotifyLyrics = await _spotifyLyrics.GetLyricsByTrackIdAsync(cleanSpotifyId);

            if (spotifyLyrics != null && spotifyLyrics.Lines.Count > 0)
            {
                _logger.LogDebug("Found Spotify lyrics for {Artist} - {Track} ({LineCount} lines)",
                    artistName, trackName, spotifyLyrics.Lines.Count);

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
}
