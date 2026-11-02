using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/items/details")]
public sealed class ItemDetailsController(ItemDetailsReader reader) : ClientApiControllerBase
{
    [HttpPost]
    [EndpointName("getItemDetails")]
    [ProducesResponseType<ItemDetailsResponse>(200)]
    public async Task<ActionResult<ItemDetailsResponse>> Post([FromBody] ItemDetailsRequest request)
    {
        if (request.Ids.Any(id => id == null)) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await reader.ReadAsync(HttpContext, Execution, request.Ids));
    }
}
