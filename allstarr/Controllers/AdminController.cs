using Microsoft.AspNetCore.Mvc;
using allstarr.Filters;

namespace allstarr.Controllers;

/// <summary>
/// Legacy AdminController - All functionality has been split into specialized controllers:
/// - ConfigController: Configuration management
/// - DiagnosticsController: System diagnostics and debugging
/// - DownloadsController: Download management
/// - PlaylistController: Playlist operations
/// - JellyfinAdminController: Jellyfin-specific operations
/// - SpotifyAdminController: Spotify-specific operations
/// - LyricsController: Lyrics management
/// - MappingController: Track mapping management
/// </summary>
[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AdminController : ControllerBase
{
    private readonly ILogger<AdminController> _logger;

    public AdminController(ILogger<AdminController> logger)
    {
        _logger = logger;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", message = "Admin API is running" });
    }
}
