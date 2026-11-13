using allstarr.Core.ClientApi;
using allstarr.Core.Intelligence;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/me")]
public sealed class MeActivityController(ListeningHistoryImportService imports, ListeningHistoryResolution resolution) : ClientApiControllerBase
{
    [HttpGet("history/imports")]
    [EndpointName("getMyHistoryImports")]
    [ProducesResponseType<IReadOnlyList<ListeningHistoryImportPreviewResult>>(200)]
    public async Task<ActionResult<IReadOnlyList<ListeningHistoryImportPreviewResult>>> Imports()
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await imports.ListAsync(Scope, 50, HttpContext.RequestAborted));
    }

    [HttpGet("history/imports/recheck")]
    [EndpointName("getMyHistoryRecheck")]
    [ProducesResponseType<HistoryRecheck>(200)]
    public async Task<ActionResult<HistoryRecheck>> Recheck()
    {
        Response.Headers.CacheControl = "private, no-store";
        try { return Ok(await resolution.RecheckAsync(Execution, HttpContext.RequestAborted)); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    private IntelligenceScope Scope => new(Execution.Principal!.UserId, Execution.Protocol.ToString().ToLowerInvariant(), Execution.BackendInstanceId);
}
