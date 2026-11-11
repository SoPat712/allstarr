using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/mixes")]
public sealed class MixesController(MixReader reader, MixCoverImageService covers) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("listMixes")]
    [EndpointDescription("Lists mixes for the listener. Service feeds from connected accounts are included only when added to apps.")]
    [ProducesResponseType<ClientMixes>(200)]
    public Task<ActionResult<ClientMixes>> List([FromQuery] string? timeZoneId = null) =>
        ExecuteAsync(() => reader.ListAsync(HttpContext, Execution, timeZoneId));

    [HttpGet("{mixId}/items")]
    [EndpointName("listMixItems")]
    [EndpointDescription("Returns the items in a mix. Unknown mixes and service feeds not added to apps return 404.")]
    [ProducesResponseType<ClientMixItems>(200)]
    public Task<ActionResult<ClientMixItems>> Items(Guid mixId) =>
        ExecuteAsync(() => reader.ItemsAsync(HttpContext, Execution, mixId));

    [HttpGet("{mixId}/cover")]
    [EndpointName("getMixCover")]
    [EndpointDescription("Serves the cover image for a mix. Unknown mixes and service feeds not added to apps return 404.")]
    [ProducesResponseType(typeof(byte[]), 200, "image/jpeg", "image/png")]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Cover(Guid mixId, [FromQuery(Name = "v")] string? version = null)
    {
        try
        {
            await reader.EnsureCoverAccessAsync(Execution, mixId);
            var owner = Execution.RequireActor().EffectiveUserId!.Value;
            if (version != null && await covers.TryCreateCachedResponseAsync(mixId, owner, version, HttpContext) is { } cached) return cached;
            return await covers.CreateResponseAsync(await reader.CoverAsync(Execution, mixId), HttpContext, version, owner);
        }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action) where T : class
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await action()); }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
