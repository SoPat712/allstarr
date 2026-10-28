using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/play-traces")]
[ServiceFilter(typeof(AdminPortFilter))]
[RequestSizeLimit(16384)]
public sealed class PlayTracesController(PlayTraceService traces, ITrackMatchRepository matches,
    AdminProtocolExecutionContextFactory contexts, ProblemReportService reports) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List([FromQuery] int limit = 50, [FromQuery] Guid? before = null, CancellationToken token = default) =>
        ExecuteAsync(async session => Document(await traces.ListAsync(session, limit, token, before)));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken token) =>
        ExecuteAsync(async session => Document(await traces.DetailAsync(session, id, token)));

    [HttpGet("{id:guid}/diagnostics")]
    public Task<IActionResult> Diagnostics(Guid id, CancellationToken token) =>
        ExecuteAsync(async session => Document(await traces.DiagnosticsAsync(session, id, token)));

    [HttpPost("{id:guid}/report")]
    public Task<IActionResult> Report(Guid id, [FromBody] PlayTraceReportRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        var trace = await traces.DetailAsync(session, id, token);
        var receipt = await reports.CreatePlaybackAsync(session, request, trace, token);
        return StatusCode(receipt.Replayed ? 200 : 201, receipt);
    });

    [HttpPost("{id:guid}/version")]
    public Task<IActionResult> Version(Guid id, [FromBody] PlayTraceVersionRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        var trace = await traces.DetailAsync(session, id, token);
        RequireScope(session, request.AuthorityScope);
        var snapshot = trace.Resolution.ExternalSnapshotId ?? throw ProblemReportException.Missing();
        var result = await matches.ResolveSnapshotAsync(new(session.AllstarrUserId!.Value, session.IsAdministrator), snapshot,
            new(request.TargetType, request.LibraryTrackId, ExternalProvider: request.ExternalProvider, ExternalId: request.ExternalId,
                Reason: "Playback version choice", AuthorityScope: request.AuthorityScope, ExpectedAuthority: request.ExpectedAuthority),
            HttpContext.TraceIdentifier, token);
        return result.Succeeded ? Document(result) : Failure(result.Failure);
    });

    [HttpPost("{id:guid}/forget")]
    public Task<IActionResult> Forget(Guid id, [FromBody] PlayTraceForgetRequest request, CancellationToken token) => ExecuteAsync(async session =>
    {
        var trace = await traces.DetailAsync(session, id, token);
        RequireScope(session, request.AuthorityScope);
        var snapshot = trace.Resolution.ExternalSnapshotId ?? throw ProblemReportException.Missing();
        var authority = await traces.AuthorityAsync(session, snapshot, request.AuthorityScope, token);
        var execution = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token);
        TrackRematchCommandResult result;
        if (authority != null)
        {
            var expected = request.ExpectedAuthority ?? (trace.Resolution.AuthorityScope == request.AuthorityScope && trace.Resolution.AuthorityId.HasValue
                ? new ManualAuthorityRevision(trace.Resolution.AuthorityId.Value, trace.Resolution.AuthorityRevision ?? 0) : null);
            if (expected == null || expected.Id != authority.Id || expected.Revision != authority.Revision)
                return Conflict(new { code = "match_changed", error = "The match changed. Refresh it before saving." });
            var kind = authority.Decision == allstarr.Core.Storage.ManualOverrideDecision.Reject ? ManualTrackAuthorityKind.Rejection :
                authority.TargetProviderId == null ? ManualTrackAuthorityKind.LocalMatch : ManualTrackAuthorityKind.ProviderMatch;
            result = await matches.RematchManualAuthorityAsync(execution, snapshot, kind, expected.Id, expected.Revision,
                HttpContext.TraceIdentifier, token, request.AuthorityScope);
        }
        else
        {
            if (request.ExpectedAuthority != null) return Conflict(new { code = "match_changed", error = "The match changed. Refresh it before saving." });
            result = await matches.RematchSnapshotAsync(execution, snapshot, HttpContext.TraceIdentifier, "manual-rematch-v3", token);
        }
        return result.Succeeded ? Document(result) : Failure(result.Failure);
    });

    private static IActionResult Document(object value) => new JsonResult(value, PlayTraceService.JsonOptions);

    private static void RequireScope(AdminAuthSession session, string scope)
    {
        if (scope is not ("personal" or "household")) throw ProblemReportException.Invalid();
        if (scope == "household" && !session.IsAdministrator)
            throw new ProblemReportException(403, "administrator_required", "Administrator access is required.");
    }
    private IActionResult Failure(TrackMatchCommandFailure failure) => StatusCode(failure switch
    {
        TrackMatchCommandFailure.NotFound => 404,
        TrackMatchCommandFailure.Forbidden => 403,
        TrackMatchCommandFailure.Invalid => 400,
        TrackMatchCommandFailure.Conflict => 409,
        TrackMatchCommandFailure.LocalLibraryUnavailable => 503,
        _ => 500
    }, new { code = "match_unavailable", error = "The match could not be changed. Refresh it and try again." });

    private async Task<IActionResult> ExecuteAsync(Func<AdminAuthSession, Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession session)
            return Unauthorized(new { code = "authentication_required", error = "Sign in again to continue." });
        try { return await action(session); }
        catch (ProblemReportException error) { return StatusCode(error.StatusCode, new { code = error.Code, error = error.Message }); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { code = "identity_unavailable", error = "The linked identity is unavailable." }); }
    }
}
