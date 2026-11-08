using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/items/{itemId}/similar")]
public sealed class SimilarAlbumsController(SimilarAlbumsReader reader) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getSimilarAlbums")]
    [ProducesResponseType<ClientSimilarAlbums>(200)]
    public async Task<ActionResult<ClientSimilarAlbums>> Get(string itemId, [FromQuery] int limit = 12)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await reader.ReadAsync(HttpContext, Execution, itemId, limit)); }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
