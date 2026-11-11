using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/batch")]
public sealed class BatchController(V2BatchDispatcher dispatcher) : ClientApiV2ControllerBase
{
    [HttpPost]
    [EndpointName("batch")]
    [EndpointDescription("Runs up to 20 v2 reads in one round trip and returns each one's status and body by its `id`. Only v2 GET " +
        "routes can be batched. One failing read never fails the others.")]
    [ProducesResponseType<V2BatchResponse>(200)]
    public async Task<IActionResult> Post([FromBody] V2BatchRequest request)
    {
        if (request.Requests.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != request.Requests.Length) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await dispatcher.RunAsync(HttpContext, Execution, request.Requests));
    }
}
