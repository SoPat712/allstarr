using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Models.Admin;
using allstarr.Services.Spotify;
using allstarr.Services.Common;
using allstarr.Services;
using allstarr.Services.Admin;
using allstarr.Filters;
using allstarr.Core.Matching;
using allstarr.Core.Settings;
using System.Text.Json;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class SpotifyAdminController : ControllerBase
{
    private readonly ILogger<SpotifyAdminController> _logger;
    private readonly SpotifyApiClient _spotifyClient;
    private readonly SpotifyApiClientFactory _spotifyClientFactory;
    private readonly SpotifySessionCookieService _spotifySessionCookieService;
    private readonly IApplicationCache _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;

    public SpotifyAdminController(
        ILogger<SpotifyAdminController> logger,
        SpotifyApiClient spotifyClient,
        SpotifyApiClientFactory spotifyClientFactory,
        SpotifySessionCookieService spotifySessionCookieService,
        IApplicationCache cache,
        IServiceProvider serviceProvider,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings)
    {
        _logger = logger;
        _spotifyClient = spotifyClient;
        _spotifyClientFactory = spotifyClientFactory;
        _spotifySessionCookieService = spotifySessionCookieService;
        _cache = cache;
        _serviceProvider = serviceProvider;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
    }

    [HttpGet("spotify/user-playlists")]
    public async Task<IActionResult> GetSpotifyUserPlaylists([FromQuery] string? userId = null)
    {
        if (!_spotifyApiSettings.Enabled)
        {
            return BadRequest(new { error = "Spotify API is not enabled." });
        }

        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionObj) ||
            sessionObj is not AdminAuthSession session)
        {
            return Unauthorized(new { error = "Authentication required" });
        }

        var requestedUserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        if (!session.IsAdministrator)
        {
            if (!string.IsNullOrWhiteSpace(requestedUserId) &&
                !requestedUserId.Equals(session.UserId, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "You can only access your own playlist links" });
            }

            requestedUserId = session.UserId;
        }

        var cookieScopeUserId = requestedUserId ?? session.UserId;
        var sessionCookie = await _spotifySessionCookieService.ResolveSessionCookieAsync(cookieScopeUserId);
        if (string.IsNullOrWhiteSpace(sessionCookie))
        {
            return BadRequest(new
            {
                error = "No Spotify session cookie configured for this user.",
                message = "Set a user-scoped sp_dc cookie via POST /api/admin/spotify/session-cookie."
            });
        }

        SpotifyApiClient spotifyClient = _spotifyClient;
        SpotifyApiClient? scopedSpotifyClient = null;

        if (!string.Equals(sessionCookie, _spotifyApiSettings.SessionCookie, StringComparison.Ordinal))
        {
            scopedSpotifyClient = _spotifyClientFactory.Create(sessionCookie);
            spotifyClient = scopedSpotifyClient;
        }

        try
        {
            // Get list of already-configured Spotify playlist IDs in the selected ownership scope.
            var configuredPlaylists = _spotifyImportSettings.Playlists;
            if (session.TenantId is { } tenantId &&
                HttpContext.RequestServices.GetService<IDurableRuntimeSettings>() is { } settings)
            {
                var current = await settings.GetAsync(
                    tenantId,
                    "SpotifyImport:Playlists",
                    HttpContext.RequestAborted);
                if (current.Value is string json && !string.IsNullOrWhiteSpace(json))
                {
                    configuredPlaylists = SpotifyPlaylistConfigParser.Parse(json);
                }
            }

            var scopedConfiguredPlaylists = configuredPlaylists.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(requestedUserId))
            {
                scopedConfiguredPlaylists = scopedConfiguredPlaylists.Where(p =>
                    string.IsNullOrWhiteSpace(p.UserId) ||
                    p.UserId.Equals(requestedUserId, StringComparison.OrdinalIgnoreCase));
            }

            var linkedSpotifyIds = new HashSet<string>(
                scopedConfiguredPlaylists.Select(p => p.Id),
                StringComparer.OrdinalIgnoreCase
            );

            // Use SpotifyApiClient's GraphQL method - much less rate-limited than REST API
            var spotifyPlaylists = await spotifyClient.GetUserPlaylistsAsync(searchName: null);

            if (spotifyPlaylists == null || spotifyPlaylists.Count == 0)
            {
                return Ok(new { playlists = new List<object>() });
            }

            var playlists = spotifyPlaylists.Select(p => new
            {
                id = p.SpotifyId,
                name = p.Name,
                trackCount = p.TotalTracks,
                owner = p.OwnerName ?? "",
                isPublic = p.Public,
                isLinked = linkedSpotifyIds.Contains(p.SpotifyId)
            }).ToList();

            return Ok(new { playlists });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Spotify user playlists");
            return StatusCode(500, new { error = "Failed to fetch Spotify playlists" });
        }
        finally
        {
            scopedSpotifyClient?.Dispose();
        }
    }

    [HttpGet("spotify/session-cookie/status")]
    public async Task<IActionResult> GetSpotifySessionCookieStatus([FromQuery] string? userId = null)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionObj) ||
            sessionObj is not AdminAuthSession session)
        {
            return Unauthorized(new { error = "Authentication required" });
        }

        var requestedUserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        if (!session.IsAdministrator)
        {
            requestedUserId = session.UserId;
        }

        var status = await _spotifySessionCookieService.GetCookieStatusAsync(requestedUserId);
        var cookieSetDate = string.IsNullOrWhiteSpace(requestedUserId)
            ? null
            : await _spotifySessionCookieService.GetCookieSetDateAsync(requestedUserId);

        return Ok(new
        {
            userId = requestedUserId ?? session.UserId,
            hasCookie = status.HasCookie,
            usingGlobalFallback = status.UsingGlobalFallback,
            cookieSetDate = cookieSetDate?.ToString("o")
        });
    }

    /// <summary>
    /// Clear Spotify playlist cache to force re-matching.
    /// </summary>
    [HttpPost("spotify/clear-cache")]
    public async Task<IActionResult> ClearSpotifyCache()
    {
        try
        {
            var clearedKeys = new List<string>();

            // Clear shared cache entries for all configured playlists.
            foreach (var playlist in _spotifyImportSettings.Playlists)
            {
                var keys = new[]
                {
                    CacheKeyBuilder.BuildSpotifyPlaylistKey(playlist.Name),
                    CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlist.Name),
                    CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlist.Name)
                };

                foreach (var key in keys)
                {
                    await _cache.DeleteAsync(key);
                    clearedKeys.Add(key);
                }
            }

            _logger.LogDebug("Cleared Spotify cache for {Count} keys via admin endpoint", clearedKeys.Count);

            return Ok(new
            {
                message = "Spotify cache cleared successfully",
                clearedKeys = clearedKeys,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing Spotify cache");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }



}
