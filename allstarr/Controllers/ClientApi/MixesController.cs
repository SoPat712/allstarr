using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/mixes")]
public sealed class MixesController(MixReader reader, MixCoverImageService covers) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("listMixes")]
    [ProducesResponseType<ClientMixes>(200)]
    public Task<ActionResult<ClientMixes>> List([FromQuery] string? timeZoneId = null) =>
        ExecuteAsync(() => reader.ListAsync(HttpContext, Execution, timeZoneId));

    [HttpGet("{mixId}/items")]
    [EndpointName("listMixItems")]
    [ProducesResponseType<ClientMixItems>(200)]
    public Task<ActionResult<ClientMixItems>> Items(Guid mixId) =>
        ExecuteAsync(() => reader.ItemsAsync(HttpContext, Execution, mixId));

    [HttpGet("{mixId}/cover")]
    [EndpointName("getMixCover")]
    [ProducesResponseType(typeof(byte[]), 200, "image/png")]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Cover(Guid mixId)
    {
        try { return covers.CreateResponse(await reader.CoverAsync(Execution, mixId), HttpContext); }
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
