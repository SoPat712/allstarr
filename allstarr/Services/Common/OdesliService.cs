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
    private readonly RedisCacheService _cache;

    public OdesliService(
        IHttpClientFactory httpClientFactory,
        ILogger<OdesliService> logger,
        RedisCacheService cache)
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
        var cacheKey = $"odesli:tidal-to-spotify:{tidalTrackId}";
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
                            _logger.LogInformation("✓ Converted Tidal/{TidalId} → Spotify ID {SpotifyId}", tidalTrackId, spotifyId);
                            
                            // Cache for 7 days
                            await _cache.SetAsync(cacheKey, spotifyId, TimeSpan.FromDays(7));
                            
                            return spotifyId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to convert Tidal track to Spotify ID via Odesli");
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
        var cacheKey = $"odesli:url-to-spotify:{musicUrl}";
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
                            _logger.LogInformation("✓ Converted URL → Spotify ID {SpotifyId}", spotifyId);
                            
                            // Cache for 7 days
                            await _cache.SetAsync(cacheKey, spotifyId, TimeSpan.FromDays(7));
                            
                            return spotifyId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to convert URL to Spotify ID via Odesli");
        }

        return null;
    }
}
