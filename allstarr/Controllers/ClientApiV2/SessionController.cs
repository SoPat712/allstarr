using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/session")]
public sealed class SessionController(V2SessionReader session) : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("getSession")]
    [EndpointDescription("Reads this listener's identity, server version, available v2 areas, the dashboard link, the library's own mark and how adds to the library work. Can also be read inside a batch.")]
    [ProducesResponseType<V2SessionResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get() => Respond(await session.ReadAsync(HttpContext, Execution));
}
