using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/capabilities")]
public sealed class CapabilitiesController(LibraryProviderResolver library, ListeningStatsService? stats = null, TasteService? taste = null) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getCapabilities")]
    [ProducesResponseType<ClientCapabilities>(200)]
    public async Task<ActionResult<ClientCapabilities>> Get() =>
        Ok(new ClientCapabilities(AppVersion.Version, 1, new() { Stats = stats != null, Taste = taste != null },
            await library.ResolveAsync(HttpContext, HttpContext.GetProtocolExecutionContext()!)));
}
