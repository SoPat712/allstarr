using allstarr.Core.Artwork;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/me")]
public sealed class MeActivityController(PlayTraceService traces, ListeningHistoryImportService imports,
    ListeningHistoryResolution resolution, IProviderRegistry registry, LibraryProviderResolver library) : ClientApiControllerBase
{
    [HttpGet("plays")]
    [EndpointName("getMyPlays")]
    [ProducesResponseType<IReadOnlyList<ClientPlayTrace>>(200)]
    public async Task<ActionResult<IReadOnlyList<ClientPlayTrace>>> Plays([FromQuery] int limit = 50, [FromQuery] Guid? before = null)
    {
        Response.Headers.CacheControl = "private, no-store";
        try
        {
            var plays = await traces.ListAsync(Execution, limit, HttpContext.RequestAborted, before);
            return Ok(await ProjectAsync(plays));
        }
        catch (ProblemReportException error) { return StatusCode(error.StatusCode); }
    }

    [HttpGet("plays/{id:guid}")]
    [EndpointName("getMyPlay")]
    [ProducesResponseType<ClientPlayTrace>(200)]
    public async Task<ActionResult<ClientPlayTrace>> Play(Guid id)
    {
        Response.Headers.CacheControl = "private, no-store";
        try { return Ok((await ProjectAsync([await traces.DetailAsync(Execution, id, HttpContext.RequestAborted)]))[0]); }
        catch (ProblemReportException error) { return StatusCode(error.StatusCode); }
    }

    [HttpGet("history/imports")]
    [EndpointName("getMyHistoryImports")]
    [ProducesResponseType<IReadOnlyList<ListeningHistoryImportPreviewResult>>(200)]
    public async Task<ActionResult<IReadOnlyList<ListeningHistoryImportPreviewResult>>> Imports()
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await imports.ListAsync(Scope, 50, HttpContext.RequestAborted));
    }

    [HttpGet("history/imports/{id:guid}")]
    [EndpointName("getMyHistoryImport")]
    [ProducesResponseType<ListeningHistoryImportPreviewResult>(200)]
    public async Task<ActionResult<ListeningHistoryImportPreviewResult>> Import(Guid id)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await imports.ReadAsync(Scope, id, HttpContext.RequestAborted);
        return result == null ? NotFound() : Ok(result);
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

    private async Task<ClientPlayTrace[]> ProjectAsync(IReadOnlyList<PlayTraceDetail> plays)
    {
        var icons = await ProviderArtworkResolver.ResolveIconsAsync(HttpContext.RequestServices,
            plays.Select(item => item.Resolution.Provider).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), HttpContext.RequestAborted);
        var native = plays.Any(item => item.Request.ItemId is { } id && !id.StartsWith("ext-", StringComparison.Ordinal))
            ? await library.ResolveAsync(HttpContext, Execution) : null;
        return plays.Select(play =>
        {
            var id = play.Resolution.Provider;
            var provider = id != null && registry.TryGet(id, out var descriptor)
                ? icons.GetValueOrDefault(id)?.Provider(id, descriptor!.DisplayName) ?? new ItemDetailsProvider(id, descriptor!.DisplayName, null) : null;
            if (provider == null && play.Request.ItemId is { } itemId && !itemId.StartsWith("ext-", StringComparison.Ordinal)) provider = native;
            var metadata = play.Request.Presented;
            return new ClientPlayTrace(play, provider, string.IsNullOrWhiteSpace(metadata.Artist) ? null :
                ClientListeningStats.Artwork(metadata.Artist, title: metadata.Title));
        }).ToArray();
    }
}
