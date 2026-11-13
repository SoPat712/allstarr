using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/session")]
public sealed class SessionController(V2SessionReader session) : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("getSession")]
    [EndpointDescription("Reads this listener's identity, server version, available v2 areas and the dashboard link. Can also be read inside a batch.")]
    [ProducesResponseType<V2SessionResponse>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Get() => Respond(await session.ReadAsync(Execution));
}
