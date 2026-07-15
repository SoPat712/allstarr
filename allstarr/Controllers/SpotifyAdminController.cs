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
    private readonly SpotifyMappingService _mappingService;
    private readonly RedisCacheService _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly AdminHelperService _helperService;

    public SpotifyAdminController(
        ILogger<SpotifyAdminController> logger,
        SpotifyApiClient spotifyClient,
        SpotifyApiClientFactory spotifyClientFactory,
        SpotifySessionCookieService spotifySessionCookieService,
        SpotifyMappingService mappingService,
        RedisCacheService cache,
        IServiceProvider serviceProvider,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        AdminHelperService helperService)
    {
        _logger = logger;
        _spotifyClient = spotifyClient;
        _spotifyClientFactory = spotifyClientFactory;
        _spotifySessionCookieService = spotifySessionCookieService;
        _mappingService = mappingService;
        _cache = cache;
        _serviceProvider = serviceProvider;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _helperService = helperService;
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
            var configuredPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();

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

    [HttpPost("spotify/session-cookie")]
    public async Task<IActionResult> SetSpotifySessionCookie([FromBody] SetSpotifySessionCookieRequest request)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionObj) ||
            sessionObj is not AdminAuthSession session)
        {
            return Unauthorized(new { error = "Authentication required" });
        }

        var targetUserId = string.IsNullOrWhiteSpace(request.UserId)
            ? session.UserId
            : request.UserId.Trim();

        if (!session.IsAdministrator &&
            !targetUserId.Equals(session.UserId, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "You can only update your own Spotify session cookie"
            });
        }

        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            return BadRequest(new { error = "User ID is required" });
        }

        var saveResult = await _spotifySessionCookieService.SetUserSessionCookieAsync(targetUserId, request.SessionCookie);
        if (saveResult is ObjectResult { StatusCode: >= 400 } failure)
        {
            return failure;
        }

        return Ok(new
        {
            success = true,
            message = "Spotify session cookie saved for user scope.",
            userId = targetUserId
        });
    }

    /// <summary>
    /// Get all playlists from Jellyfin
    /// </summary>
    [HttpGet("spotify/sync")]
    public async Task<IActionResult> TriggerSpotifySync([FromServices] IEnumerable<IHostedService> hostedServices)
    {
        try
        {
            if (!_spotifyImportSettings.Enabled)
            {
                return BadRequest(new { error = "Spotify Import is not enabled" });
            }

            _logger.LogInformation("Manual Spotify sync triggered via admin endpoint");

            // Find the SpotifyMissingTracksFetcher service
            var fetcherService = hostedServices
                .OfType<allstarr.Services.Spotify.SpotifyMissingTracksFetcher>()
                .FirstOrDefault();

            if (fetcherService == null)
            {
                return BadRequest(new { error = "SpotifyMissingTracksFetcher service not found" });
            }

            var method = fetcherService.GetType().GetMethod("ExecuteOnceAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null)
                return StatusCode(500, new { error = "Spotify sync operation is unavailable" });
            await ((Task)method.Invoke(fetcherService, new object[] { HttpContext.RequestAborted })!)
                .WaitAsync(HttpContext.RequestAborted);
            _logger.LogInformation("Manual Spotify sync completed successfully");

            return Ok(new
            {
                message = "Spotify sync completed",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Spotify sync");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Manual trigger endpoint to force Spotify track matching.
    /// </summary>
    [HttpGet("spotify/match")]
    public async Task<IActionResult> TriggerSpotifyMatch([FromServices] IEnumerable<IHostedService> hostedServices)
    {
        try
        {
            if (!_spotifyApiSettings.Enabled)
            {
                return BadRequest(new { error = "Spotify API is not enabled" });
            }

            _logger.LogInformation("Manual Spotify track matching triggered via admin endpoint");

            // Find the SpotifyTrackMatchingService
            var matchingService = hostedServices
                .OfType<allstarr.Services.Spotify.SpotifyTrackMatchingService>()
                .FirstOrDefault();

            if (matchingService == null)
            {
                return BadRequest(new { error = "SpotifyTrackMatchingService not found" });
            }

            var method = matchingService.GetType().GetMethod("ExecuteOnceAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null)
                return StatusCode(500, new { error = "Spotify matching operation is unavailable" });
            await ((Task)method.Invoke(matchingService, new object[] { HttpContext.RequestAborted })!)
                .WaitAsync(HttpContext.RequestAborted);
            _logger.LogInformation("Manual Spotify track matching completed successfully");

            return Ok(new
            {
                message = "Spotify track matching completed",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Spotify track matching");
            return StatusCode(500, new { error = "Internal server error" });
        }
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

            // Clear Redis cache for all configured playlists
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



    /// <summary>
    /// Gets endpoint usage statistics from the log file.
    /// </summary>
    [HttpGet("spotify/mappings")]
    public async Task<IActionResult> GetSpotifyMappings(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool enrichMetadata = true,
        [FromQuery] string? targetType = null,
        [FromQuery] string? source = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortOrder = "asc")
    {
        try
        {
            // Get all mappings (we'll filter and sort in memory for now)
            var allMappings = await _mappingService.GetAllMappingsAsync(0, int.MaxValue);
            var stats = await _mappingService.GetStatsAsync();

            // Enrich metadata for external tracks that are missing it
            if (enrichMetadata)
            {
                await EnrichExternalMappingsMetadataAsync(allMappings);
            }

            // Apply filters
            var filteredMappings = allMappings.AsEnumerable();

            if (!string.IsNullOrEmpty(targetType) && targetType != "all")
            {
                filteredMappings = filteredMappings.Where(m =>
                    m.TargetType.Equals(targetType, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(source) && source != "all")
            {
                filteredMappings = filteredMappings.Where(m =>
                    m.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(search))
            {
                var searchLower = search.ToLower();
                filteredMappings = filteredMappings.Where(m =>
                    m.SpotifyId.ToLower().Contains(searchLower) ||
                    (m.Metadata?.Title?.ToLower().Contains(searchLower) ?? false) ||
                    (m.Metadata?.Artist?.ToLower().Contains(searchLower) ?? false));
            }

            // Apply sorting
            if (!string.IsNullOrEmpty(sortBy))
            {
                var isDescending = sortOrder?.ToLower() == "desc";

                filteredMappings = sortBy.ToLower() switch
                {
                    "title" => isDescending
                        ? filteredMappings.OrderByDescending(m => m.Metadata?.Title ?? "")
                        : filteredMappings.OrderBy(m => m.Metadata?.Title ?? ""),
                    "artist" => isDescending
                        ? filteredMappings.OrderByDescending(m => m.Metadata?.Artist ?? "")
                        : filteredMappings.OrderBy(m => m.Metadata?.Artist ?? ""),
                    "spotifyid" => isDescending
                        ? filteredMappings.OrderByDescending(m => m.SpotifyId)
                        : filteredMappings.OrderBy(m => m.SpotifyId),
                    "type" => isDescending
                        ? filteredMappings.OrderByDescending(m => m.TargetType)
                        : filteredMappings.OrderBy(m => m.TargetType),
                    "source" => isDescending
                        ? filteredMappings.OrderByDescending(m => m.Source)
                        : filteredMappings.OrderBy(m => m.Source),
                    "created" => isDescending
                        ? filteredMappings.OrderByDescending(m => m.CreatedAt)
                        : filteredMappings.OrderBy(m => m.CreatedAt),
                    _ => filteredMappings
                };
            }

            var filteredList = filteredMappings.ToList();
            var totalCount = filteredList.Count;

            // Apply pagination
            var skip = (page - 1) * pageSize;
            var pagedMappings = filteredList.Skip(skip).Take(pageSize).ToList();

            return Ok(new
            {
                mappings = pagedMappings,
                pagination = new
                {
                    page,
                    pageSize,
                    totalCount,
                    totalPages = (int)Math.Ceiling((double)totalCount / pageSize)
                },
                stats
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Spotify mappings");
            return StatusCode(500, new { error = "Failed to get mappings" });
        }
    }

    /// <summary>
    /// Gets a specific Spotify track mapping
    /// </summary>
    [HttpGet("spotify/mappings/{spotifyId}")]
    public async Task<IActionResult> GetSpotifyMapping(string spotifyId)
    {
        try
        {
            var mapping = await _mappingService.GetMappingAsync(spotifyId);
            if (mapping == null)
            {
                return NotFound(new { error = "Mapping not found" });
            }

            return Ok(mapping);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Spotify mapping for {SpotifyId}", spotifyId);
            return StatusCode(500, new { error = "Failed to get mapping" });
        }
    }

    /// <summary>
    /// Creates or updates a Spotify track mapping (manual override)
    /// </summary>
    [HttpPost("spotify/mappings")]
    public async Task<IActionResult> SaveSpotifyMapping([FromBody] SpotifyMappingRequest request)
    {
        try
        {
            var metadata = request.Metadata != null ? new TrackMetadata
            {
                Title = request.Metadata.Title,
                Artist = request.Metadata.Artist,
                Album = request.Metadata.Album,
                ArtworkUrl = request.Metadata.ArtworkUrl,
                DurationMs = request.Metadata.DurationMs
            } : null;

            var success = await _mappingService.SaveManualMappingAsync(
                request.SpotifyId,
                request.TargetType,
                request.LocalId,
                request.ExternalProvider,
                request.ExternalId,
                metadata);

            if (success)
            {
                _logger.LogInformation("Saved manual mapping: {SpotifyId} → {TargetType}",
                    request.SpotifyId, request.TargetType);
                return Ok(new { success = true });
            }

            return StatusCode(500, new { error = "Failed to save mapping" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Spotify mapping");
            return StatusCode(500, new { error = "Failed to save mapping" });
        }
    }

    /// <summary>
    /// Deletes a Spotify track mapping
    /// </summary>
    [HttpDelete("spotify/mappings/{spotifyId}")]
    public async Task<IActionResult> DeleteSpotifyMapping(
        string spotifyId,
        [FromQuery] string? provider = null)
    {
        try
        {
            var success = string.IsNullOrWhiteSpace(provider)
                ? await _mappingService.DeleteMappingAsync(spotifyId)
                : await _mappingService.RemoveExternalProviderAsync(spotifyId, provider);

            if (success)
            {
                if (string.IsNullOrWhiteSpace(provider))
                {
                    _logger.LogInformation("Deleted mapping for {SpotifyId}", spotifyId);
                }
                else
                {
                    _logger.LogInformation(
                        "Removed provider {Provider} from mapping for {SpotifyId}",
                        provider,
                        spotifyId);
                }

                return Ok(new { success = true });
            }

            return NotFound(new { error = "Mapping not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Spotify mapping for {SpotifyId}", spotifyId);
            return StatusCode(500, new { error = "Failed to delete mapping" });
        }
    }

    /// <summary>
    /// Gets statistics about Spotify track mappings
    /// </summary>
    [HttpGet("spotify/mappings/stats")]
    public async Task<IActionResult> GetSpotifyMappingStats()
    {
        try
        {
            var stats = await _mappingService.GetStatsAsync();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Spotify mapping stats");
            return StatusCode(500, new { error = "Failed to get stats" });
        }
    }

    /// <summary>
    /// Enriches metadata for external mappings that are missing title/artist/artwork
    /// </summary>
    private async Task EnrichExternalMappingsMetadataAsync(List<SpotifyTrackMapping> mappings)
    {
        var metadataService = _serviceProvider.GetService<IMusicMetadataService>();
        if (metadataService == null)
        {
            _logger.LogWarning("No metadata service available for enrichment");
            return;
        }

        foreach (var mapping in mappings)
        {
            // Skip if not external or already has metadata
            if (mapping.TargetType != "external" ||
                string.IsNullOrEmpty(mapping.ExternalProvider) ||
                string.IsNullOrEmpty(mapping.ExternalId))
            {
                continue;
            }

            // Skip if already has complete metadata
            if (mapping.Metadata != null &&
                !string.IsNullOrEmpty(mapping.Metadata.Title) &&
                !string.IsNullOrEmpty(mapping.Metadata.Artist))
            {
                continue;
            }

            try
            {
                // Fetch track details from external provider
                var song = await metadataService.GetSongAsync(mapping.ExternalProvider.ToLowerInvariant(), mapping.ExternalId);

                if (song != null)
                {
                    // Update metadata
                    if (mapping.Metadata == null)
                    {
                        mapping.Metadata = new TrackMetadata();
                    }

                    mapping.Metadata.Title = song.Title;
                    mapping.Metadata.Artist = song.Artist;
                    mapping.Metadata.Album = song.Album;
                    mapping.Metadata.ArtworkUrl = song.CoverArtUrl;
                    mapping.Metadata.DurationMs = song.Duration.HasValue ? song.Duration.Value * 1000 : null;

                    // Save enriched metadata back to cache
                    await _mappingService.SaveMappingAsync(mapping);

                    _logger.LogDebug("Enriched metadata for {SpotifyId} from {Provider}: {Title} by {Artist}",
                        mapping.SpotifyId, mapping.ExternalProvider, song.Title, song.Artist);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich metadata for {SpotifyId} from {Provider}:{ExternalId}",
                    mapping.SpotifyId, mapping.ExternalProvider, mapping.ExternalId);
            }
        }
    }

    public class SetSpotifySessionCookieRequest
    {
        public required string SessionCookie { get; set; }
        public string? UserId { get; set; }
    }

}
