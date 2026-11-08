using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/items/{itemId}/lyrics")]
public sealed class ItemLyricsController(ItemLyricsReader reader) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getItemLyrics")]
    [ProducesResponseType<RichLyrics>(200)]
    public Task<ActionResult<RichLyrics>> Get(string itemId, [FromQuery] string? sourceId = null) =>
        ExecuteAsync(() => reader.ReadAsync(HttpContext, Execution, itemId, sourceId ?? (Request.Query.ContainsKey("sourceId") ? "" : null)));

    [HttpGet("sources")]
    [EndpointName("getLyricsSources")]
    [ProducesResponseType<ClientLyricsSources>(200)]
    public Task<ActionResult<ClientLyricsSources>> Sources(string itemId, [FromQuery] bool includeLyrics = false) =>
        ExecuteAsync(() => reader.SourcesAsync(HttpContext, Execution, itemId, includeLyrics));

    [HttpPut("source")]
    [EndpointName("setLyricsSource")]
    [ProducesResponseType<ClientLyricsSelection>(200)]
    public Task<ActionResult<ClientLyricsSelection>> Set(string itemId, [FromBody] SetClientLyricsSourceRequest request) =>
        ExecuteAsync(() => reader.SetAsync(HttpContext, Execution, itemId, request));

    [HttpPost("source/clear")]
    [EndpointName("clearLyricsSource")]
    [ProducesResponseType<ClientLyricsSelection>(200)]
    public Task<ActionResult<ClientLyricsSelection>> Clear(string itemId, [FromBody] ClearClientLyricsSourceRequest request) =>
        ExecuteAsync(() => reader.ClearAsync(HttpContext, Execution, itemId, request));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T?>> action) where T : class
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await action();
            return result == null ? NotFound() : Ok(result);
        }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (LyricsChoiceConflictException) { return Conflict(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
