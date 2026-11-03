using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/playback")]
public sealed class PlaybackController(PlaybackReader reader) : ClientApiControllerBase
{
    [HttpGet("{requestId}")]
    [EndpointName("getPlayback")]
    [ProducesResponseType<ClientPlayback>(200)]
    public async Task<ActionResult<ClientPlayback>> Get(string requestId)
    {
        Response.Headers.CacheControl = "no-store";
        var playback = Guid.TryParse(requestId, out var id) ? await reader.ReadAsync(HttpContext, Execution, id) : null;
        return playback == null ? NotFound() : Ok(playback);
    }

    [HttpGet("current")]
    [EndpointName("getCurrentPlayback")]
    [ProducesResponseType<ClientPlayback>(200)]
    public async Task<ActionResult<ClientPlayback>> Current()
    {
        Response.Headers.CacheControl = "no-store";
        var playback = await reader.CurrentAsync(HttpContext, Execution);
        return playback == null ? NotFound() : Ok(playback);
    }
}
