using allstarr.Filters;
using allstarr.Core.Matching;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

/// <summary>
/// Compatibility endpoint for clearing an injected-playlist match.
/// Match listing and review are owned by <see cref="TrackMatchesController"/> and
/// persisted in PostgreSQL; this endpoint never reads or writes mapping files.
/// </summary>
[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class MappingController(
    ILogger<MappingController> logger,
    ITrackMatchRepository trackMatchCommands) : ControllerBase
{
    [HttpDelete("mappings/tracks")]
    public async Task<IActionResult> DeleteTrackMapping(
        [FromQuery] string playlist,
        [FromQuery] string spotifyId,
        [FromQuery] string? provider = null)
    {
        if (string.IsNullOrWhiteSpace(playlist) || string.IsNullOrWhiteSpace(spotifyId))
        {
            return BadRequest(new { error = "playlist and spotifyId parameters are required" });
        }

        try
        {
            if (!TrySession(out var session, out var sessionError)) return sessionError!;
            var result = await trackMatchCommands.ClearSpotifyAsync(
                new TrackMatchActor(
                    session!.TenantId!.Value,
                    session.AllstarrUserId!.Value,
                    session.IsAdministrator),
                spotifyId,
                HttpContext.TraceIdentifier,
                HttpContext.RequestAborted);
            if (!result.Succeeded && result.Failure != TrackMatchCommandFailure.NotFound)
            {
                return result.Failure switch
                {
                    TrackMatchCommandFailure.Invalid => BadRequest(new { error = result.Error }),
                    TrackMatchCommandFailure.Forbidden => StatusCode(403, new { error = result.Error }),
                    TrackMatchCommandFailure.Conflict => Conflict(new { error = result.Error }),
                    _ => StatusCode(500, new { error = result.Error ?? "Failed to clear track match" })
                };
            }

            return result.Succeeded
                ? Ok(new { success = true, message = "Mapping deleted successfully" })
                : NotFound(new { error = "Mapping not found" });
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to delete track mapping for {Playlist} - {SpotifyId}",
                playlist,
                spotifyId);
            return StatusCode(500, new { error = "Failed to delete track mapping" });
        }
    }

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null;
        error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession authenticated)
        {
            error = Unauthorized(new { error = "Authentication required" });
            return false;
        }
        if (!authenticated.TenantId.HasValue || !authenticated.AllstarrUserId.HasValue)
        {
            error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" });
            return false;
        }
        session = authenticated;
        return true;
    }
}
