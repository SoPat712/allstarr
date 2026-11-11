using allstarr.Core.ClientApi;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/capabilities")]
public sealed class CapabilitiesController(LibraryProviderResolver library, IDurableRuntimeSettings settings,
    IServiceProviderIsService services, ClientLibraryAdditions additions, IKaraokeModelService? karaoke = null,
    ListeningStatsService? stats = null, TasteService? taste = null) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getCapabilities")]
    [ProducesResponseType<ClientCapabilities>(200)]
    public async Task<ActionResult<ClientCapabilities>> Get()
    {
        var address = (await settings.GetAsync("Admin:Oidc:PublicUrl", HttpContext.RequestAborted)).NormalizedValue.Trim();
        return Ok(new ClientCapabilities(AppVersion.Version, 1, new()
        {
            Stats = stats != null,
            Taste = taste != null,
            Sound = services.IsService(typeof(SoundProfileService)),
            PlayActivity = services.IsService(typeof(PlayTraceService)),
            HistoryImports = services.IsService(typeof(ListeningHistoryImportService)),
            HistoryRecheck = services.IsService(typeof(ListeningHistoryResolution)),
            KaraokeModel = karaoke?.IsAvailable ?? false
        },
            await library.ResolveAsync(HttpContext, HttpContext.GetProtocolExecutionContext()!),
            await additions.CapabilitiesAsync(Execution, HttpContext.RequestAborted),
            string.IsNullOrEmpty(address) ? null : new ClientDashboard(address)));
    }
}
