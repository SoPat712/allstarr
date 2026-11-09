using allstarr.Core.Playback;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/stats/art")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class RecapArtworkController(AdminProtocolExecutionContextFactory contexts, RecapArtworkReader artwork) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? artist, [FromQuery] string? title = null, [FromQuery] string? album = null, CancellationToken token = default)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (string.IsNullOrWhiteSpace(artist) || artist.Length > 500 || title?.Length > 500 || album?.Length > 500) return BadRequest();
        var protocol = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token);
        var asset = await artwork.ReadAsync(protocol, artist, title, album, token);
        Response.Headers.CacheControl = asset != null ? "private, max-age=604800" : "private, max-age=3600";
        return asset != null ? File(asset.Bytes, asset.ContentType) : NoContent();
    }
}
