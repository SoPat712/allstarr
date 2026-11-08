using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/items/{itemId}/appearances")]
public sealed class ItemAppearancesController(ItemAppearancesReader reader) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getItemAppearances")]
    [ProducesResponseType<ClientItemAppearances>(200)]
    public async Task<ActionResult<ClientItemAppearances>> Get(string itemId)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await reader.ReadAsync(HttpContext, Execution, itemId)); }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
