using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/capabilities")]
public sealed class CapabilitiesController : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getCapabilities")]
    [ProducesResponseType<ClientCapabilities>(200)]
    public ActionResult<ClientCapabilities> Get() => Ok(ClientApiContract.Capabilities);
}
