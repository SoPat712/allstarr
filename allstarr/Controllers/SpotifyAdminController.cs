using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Spotify;
using allstarr.Filters;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class SpotifyAdminController : ControllerBase
{
    private readonly ILogger<SpotifyAdminController> _logger;
    private readonly SpotifySessionCookieService _spotifySessionCookieService;

    public SpotifyAdminController(
        ILogger<SpotifyAdminController> logger,
        SpotifySessionCookieService spotifySessionCookieService)
    {
        _logger = logger;
        _spotifySessionCookieService = spotifySessionCookieService;
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



}
