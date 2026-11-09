using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/me")]
public sealed class MeController(ListeningStatsService stats, TasteService taste, SoundProfileService sound,
    RecapArtworkReader artwork) : ClientApiControllerBase
{
    [HttpGet("stats")]
    [EndpointName("getMyStats")]
    [ProducesResponseType<ClientListeningStats>(200)]
    public async Task<ActionResult<ClientListeningStats>> Stats([FromQuery] string? period = null, [FromQuery] int? year = null,
        [FromQuery] string? timeZoneId = null, CancellationToken token = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        try { return Ok(new ClientListeningStats(await stats.GetAsync(Execution, period, year, token, timeZoneId))); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        catch (ArgumentException)
        {
            return BadRequest(new ClientApiError("invalid_request", "Use period=7d, 30d, year or all, or year=1 through 9998."));
        }
    }

    [HttpGet("stats/art")]
    [EndpointName("getMyStatsArtwork")]
    [ProducesResponseType(typeof(byte[]), 200, "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Art([FromQuery] string? artist, [FromQuery] string? title = null,
        [FromQuery] string? album = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(artist)) return BadRequest();
        try
        {
            var asset = await artwork.ReadAsync(Execution, artist, title, album, token);
            Response.Headers.CacheControl = asset != null ? "private, max-age=604800" : "private, max-age=3600";
            return asset != null ? File(asset.Bytes, asset.ContentType) : NoContent();
        }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        catch (ArgumentException) { return BadRequest(); }
    }

    [HttpGet("taste")]
    [EndpointName("getMyTaste")]
    [ProducesResponseType<ClientTasteProfile>(200)]
    public async Task<ActionResult<ClientTasteProfile>> Taste(CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        try
        {
            var profile = await taste.GetAsync(Execution.Principal!.UserId, token);
            return Ok(new ClientTasteProfile(ClientTasteLayer.From(profile.Lately), ClientTasteLayer.From(profile.Usually),
                profile.LatelyWeight, profile.Paused, profile.UseImportedHistory, profile.Nudges));
        }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("sound")]
    [EndpointName("getMySound")]
    [ProducesResponseType<SoundProfile>(200)]
    public async Task<ActionResult<SoundProfile>> Sound(CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        var profile = await sound.GetAsync(Execution.Principal!.UserId, token);
        return profile == null ? NotFound() : Ok(profile);
    }
}
