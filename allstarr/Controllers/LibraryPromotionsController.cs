using allstarr.Core.Enrichment;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/library-promotions")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class LibraryPromotionsController(LibraryPromotionService promotions, PromotionRoots roots,
    AdminProtocolExecutionContextFactory contexts) : ControllerBase
{
    [HttpGet("roots")]
    public Task<IActionResult> Roots(CancellationToken token) => ExecuteAsync(token, async (_, _) =>
        (await roots.ReadAsync(token)).Select(item => new { item.Id, item.Name }));

    [HttpPut("roots")]
    public Task<IActionResult> SaveRoots([FromBody] PromotionRoot[] request, CancellationToken token) => ExecuteAsync(token,
        async (session, _) => { await roots.SaveAsync(session.AllstarrUserId!.Value, request, token); return new { saved = true }; });

    [HttpPost("preview")]
    public Task<IActionResult> Preview([FromBody] PromoteSongRequest request, CancellationToken token) => ExecuteAsync(token,
        (_, context) => promotions.PreviewAsync(context, request, token));

    [HttpPost]
    public Task<IActionResult> Promote([FromBody] PromoteSongRequest request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => StartReceipt(await promotions.StartOrRequestAsync(context, request, token)), accepted: true);

    [HttpPost("requests")]
    public Task<IActionResult> CreateRequest([FromBody] PromoteSongRequest request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => StartReceipt(await promotions.StartOrRequestAsync(context, request, token, requestApproval: true)), accepted: true);

    [HttpGet("requests")]
    public Task<IActionResult> Requests([FromQuery] string? state, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => (await promotions.ListRequestsAsync(context, state, token)).Select(RequestReceipt));

    [HttpGet("requests/{id:guid}")]
    public Task<IActionResult> GetRequest(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => RequestReceipt(await promotions.GetRequestAsync(context, id, token)));

    [HttpPost("requests/{id:guid}/approve")]
    public Task<IActionResult> ApproveRequest(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => RequestReceipt(await promotions.ApproveRequestAsync(context, id, token)), accepted: true);

    [HttpPost("requests/{id:guid}/decline")]
    public Task<IActionResult> DeclineRequest(Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PromotionRequestDecline? request,
        CancellationToken token) => ExecuteAsync(token,
        async (_, context) => RequestReceipt(await promotions.DeclineRequestAsync(context, id, request?.Note, token)));

    [HttpPost("requests/{id:guid}/cancel")]
    public Task<IActionResult> CancelRequest(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => RequestReceipt(await promotions.CancelRequestAsync(context, id, token)));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => Receipt(await promotions.GetAsync(context, id, token)));

    [HttpPost("{id:guid}/approve")]
    public Task<IActionResult> Approve(Guid id, [FromBody] PromotionApproval request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => Receipt(await promotions.ApproveAsync(context, id, request.Reason, token)), accepted: true);

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => Receipt(await promotions.CancelAsync(context, id, token)));

    [HttpPost("{id:guid}/songs/{recordingId:guid}/review")]
    public Task<IActionResult> Review(Guid id, Guid recordingId, [FromBody] PromotionSongReview request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => Receipt(await promotions.ReviewAsync(context, id, recordingId, request.Action, request.Reason, token)), accepted: true);

    private static object Receipt(Core.Storage.PromotionRunRecord run) => new
    { run.Id, run.JobId, run.Step, run.Outcome, run.Reason, run.ManagedFileId, run.ApprovedByUserId, run.ApprovalReason, run.Tracks };

    private static object StartReceipt(PromotionStartResult result) => result.Run is { } run
        ? Receipt(run) : new { requested = true, request = RequestReceipt(result.Request!) };

    private static object RequestReceipt(Core.Storage.PromotionRequestRecord request) => new
    {
        request.Id,
        request.RequesterUserId,
        request.RecordingId,
        request.ReleaseIdentity,
        request.Title,
        request.Artist,
        request.Year,
        request.SongCount,
        request.CoverReference,
        request.Unit,
        request.State,
        request.DecidedByUserId,
        request.DecidedAt,
        request.DeclineNote,
        request.RunId,
        request.CreatedAt
    };

    private async Task<IActionResult> ExecuteAsync<T>(CancellationToken token,
        Func<AdminAuthSession, ProtocolExecutionContext, Task<T>> action, bool accepted = false)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        if (session.AllstarrUserId == null) return StatusCode(403, new { error = "A linked backend user is required." });
        try
        {
            var context = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token);
            var result = await action(session, context);
            return accepted ? Accepted(result) : Ok(result);
        }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "The account or library path is not authorized for this promotion." }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { error = "The promotion request or root configuration is invalid." }); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return Conflict(new { error = "The source, destination or promotion changed. Review the current status before trying again." }); }
    }
}

public sealed record PromotionApproval(string Reason);
public sealed record PromotionSongReview(string Action, string? Reason = null);
public sealed record PromotionRequestDecline(string? Note = null);
