using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/capabilities")]
public sealed class CapabilitiesController(LibraryProviderResolver library) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getCapabilities")]
    [ProducesResponseType<ClientCapabilities>(200)]
    public async Task<ActionResult<ClientCapabilities>> Get() =>
        Ok(new ClientCapabilities(AppVersion.Version, 1, new(),
            await library.ResolveAsync(HttpContext, HttpContext.GetProtocolExecutionContext()!)));
}
