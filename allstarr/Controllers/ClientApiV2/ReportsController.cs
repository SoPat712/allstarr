using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/reports")]
[RequestSizeLimit(16384)]
public sealed class ReportsController(V2LibraryEndpoints library) : ClientApiV2ControllerBase
{
    [HttpPost]
    [EndpointName("createV2Report")]
    [EndpointDescription("Creates a listener problem report. Optional playback context must belong to this listener and device; foreign playback ids return 404.")]
    [ProducesResponseType<V2ReportReceipt>(201)]
    public async Task<IActionResult> Create([FromBody] V2CreateReport request) => Respond(await library.ReportAsync(Execution, request));
}
