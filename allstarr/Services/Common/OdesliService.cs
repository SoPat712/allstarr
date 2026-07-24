using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

/// <summary>
/// Service for converting music URLs between platforms using Odesli/song.link API
/// </summary>
public class OdesliService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OdesliService> _logger;
    private readonly IApplicationCache _cache;

    public OdesliService(
        IHttpClientFactory httpClientFactory,
        ILogger<OdesliService> logger,
        IApplicationCache cache)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Converts a Tidal track ID to a Spotify track ID using Odesli
    /// Results are cached for 7 days
    /// </summary>
    public async Task<string?> ConvertTidalToSpotifyIdAsync(string tidalTrackId, CancellationToken cancellationToken = default)
    {
        // Check cache first (7 day TTL - these mappings don't change)
        var cacheKey = CacheKeyBuilder.BuildOdesliTidalToSpotifyKey(tidalTrackId);
        var cached = await _cache.GetAsync<string>(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            _logger.LogDebug("✓ Using cached Spotify ID for Tidal track {TidalId}", tidalTrackId);
            return cached;
        }

        try
        {
            var tidalUrl = $"https://tidal.com/browse/track/{tidalTrackId}";
            var odesliUrl = $"https://api.song.link/v1-alpha.1/links?url={Uri.EscapeDataString(tidalUrl)}&userCountry=US";

            _logger.LogDebug("🔗 Converting Tidal track {TidalId} to Spotify ID via Odesli", tidalTrackId);

            var odesliResponse = await _httpClient.GetAsync(odesliUrl, cancellationToken);
            if (odesliResponse.IsSuccessStatusCode)
            {
                var odesliJson = await odesliResponse.Content.ReadAsStringAsync(cancellationToken);
                var odesliDoc = JsonDocument.Parse(odesliJson);

                // Extract Spotify track ID from the Spotify URL
                if (odesliDoc.RootElement.TryGetProperty("linksByPlatform", out var platforms) &&
                    platforms.TryGetProperty("spotify", out var spotifyPlatform) &&
                    spotifyPlatform.TryGetProperty("url", out var spotifyUrlEl))
                {
                    var spotifyUrl = spotifyUrlEl.GetString();
                    if (!string.IsNullOrEmpty(spotifyUrl))
                    {
                        // Extract ID from URL: https://open.spotify.com/track/{id}
                        var match = System.Text.RegularExpressions.Regex.Match(spotifyUrl, @"spotify\.com/track/([a-zA-Z0-9]+)");
                        if (match.Success)
                        {
                            var spotifyId = match.Groups[1].Value;
                            _logger.LogDebug("✓ Converted Tidal/{TidalId} → Spotify ID {SpotifyId}", tidalTrackId, spotifyId);

                            // Cache for configurable duration
                            await _cache.SetAsync(cacheKey, spotifyId, CacheExtensions.OdesliLookupTTL);

                            return spotifyId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert Tidal track to Spotify ID via Odesli");
        }

        return null;
    }

    /// <summary>
    /// Converts any music URL to a Spotify track ID using Odesli
    /// Results are cached for 7 days
    /// </summary>
    public async Task<string?> ConvertUrlToSpotifyIdAsync(string musicUrl, CancellationToken cancellationToken = default)
    {
        // Check cache first
        var cacheKey = CacheKeyBuilder.BuildOdesliUrlToSpotifyKey(musicUrl);
        var cached = await _cache.GetAsync<string>(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            _logger.LogDebug("✓ Using cached Spotify ID for URL {Url}", musicUrl);
            return cached;
        }

        try
        {
            var odesliUrl = $"https://api.song.link/v1-alpha.1/links?url={Uri.EscapeDataString(musicUrl)}&userCountry=US";

            _logger.LogDebug("🔗 Converting URL to Spotify ID via Odesli: {Url}", musicUrl);

            var odesliResponse = await _httpClient.GetAsync(odesliUrl, cancellationToken);
            if (odesliResponse.IsSuccessStatusCode)
            {
                var odesliJson = await odesliResponse.Content.ReadAsStringAsync(cancellationToken);
                var odesliDoc = JsonDocument.Parse(odesliJson);

                // Extract Spotify track ID from the Spotify URL
                if (odesliDoc.RootElement.TryGetProperty("linksByPlatform", out var platforms) &&
                    platforms.TryGetProperty("spotify", out var spotifyPlatform) &&
                    spotifyPlatform.TryGetProperty("url", out var spotifyUrlEl))
                {
                    var spotifyUrl = spotifyUrlEl.GetString();
                    if (!string.IsNullOrEmpty(spotifyUrl))
                    {
                        // Extract ID from URL: https://open.spotify.com/track/{id}
                        var match = System.Text.RegularExpressions.Regex.Match(spotifyUrl, @"spotify\.com/track/([a-zA-Z0-9]+)");
                        if (match.Success)
                        {
                            var spotifyId = match.Groups[1].Value;
                            _logger.LogDebug("✓ Converted URL → Spotify ID {SpotifyId}", spotifyId);

                            // Cache for configurable duration
                            await _cache.SetAsync(cacheKey, spotifyId, CacheExtensions.OdesliLookupTTL);

                            return spotifyId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert URL to Spotify ID via Odesli");
        }

        return null;
    }

    /// <summary>
    /// Translates a track URL from a source provider to a target provider's track ID.
    /// </summary>
    public async Task<string?> TranslateTrackUrlAsync(string sourceUrl, string targetProvider, CancellationToken cancellationToken = default)
    {
        var targetPlatform = targetProvider.ToLowerInvariant() switch
        {
            "spotify" => "spotify",
            "applemusic" or "apple-download" => "appleMusic",
            "deezer" => "deezer",
            "qobuz" => "qobuz",
            "squidwtf" => "tidal",
            "tidal" => "tidal",
            _ => null
        };

        if (targetPlatform == null) return null;

        var cacheKey = CacheKeyBuilder.BuildOdesliTranslationKey(sourceUrl, targetPlatform);
        var cached = await _cache.GetAsync<string>(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        try
        {
            var odesliUrl = $"https://api.song.link/v1-alpha.1/links?url={Uri.EscapeDataString(sourceUrl)}&userCountry=US";
            _logger.LogDebug("🔗 Odesli: Translating {Url} to platform {Platform}", sourceUrl, targetPlatform);

            var response = await _httpClient.GetAsync(odesliUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("linksByPlatform", out var platforms) &&
                    platforms.TryGetProperty(targetPlatform, out var platformObj) &&
                    platformObj.TryGetProperty("url", out var urlEl))
                {
                    var targetUrl = urlEl.GetString();
                    if (!string.IsNullOrEmpty(targetUrl))
                    {
                        var targetId = ExtractTrackIdFromUrl(targetUrl, targetPlatform);
                        if (!string.IsNullOrEmpty(targetId))
                        {
                            await _cache.SetAsync(cacheKey, targetId, CacheExtensions.OdesliLookupTTL);
                            return targetId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to translate URL {Url} to {Platform} via Odesli", sourceUrl, targetPlatform);
        }

        return null;
    }

    private static string? ExtractTrackIdFromUrl(string url, string platform)
    {
        if (string.IsNullOrEmpty(url)) return null;

        if (platform == "spotify")
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"spotify\.com/track/([a-zA-Z0-9]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        if (platform == "appleMusic")
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]i=([0-9]+)");
            if (match.Success) return match.Groups[1].Value;

            match = System.Text.RegularExpressions.Regex.Match(url, @"/song/([0-9]+)");
            if (match.Success) return match.Groups[1].Value;
        }
        if (platform == "deezer")
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/track/([0-9]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        if (platform == "qobuz")
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/track/([0-9]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        if (platform == "tidal")
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/track/([0-9]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        return null;
    }
}
