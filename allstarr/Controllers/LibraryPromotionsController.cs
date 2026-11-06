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

    [HttpGet]
    public Task<IActionResult> List([FromQuery] string state = "active", [FromQuery] int limit = 30,
        [FromQuery] string? cursor = null, CancellationToken token = default) => ExecuteAsync(token,
        (_, context) => promotions.ListAsync(context, state, limit, cursor, token));

    [HttpGet("kept")]
    public Task<IActionResult> Kept(CancellationToken token) => ExecuteAsync(token,
        (_, context) => promotions.KeptAsync(context, token));

    [HttpPost]
    public Task<IActionResult> Promote([FromBody] PromoteSongRequest request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await StartReceiptAsync(context, await promotions.StartOrRequestAsync(context, request, token), token), accepted: true);

    [HttpPost("requests")]
    public Task<IActionResult> CreateRequest([FromBody] PromoteSongRequest request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await StartReceiptAsync(context, await promotions.StartOrRequestAsync(context, request, token, requestApproval: true), token), accepted: true);

    [HttpGet("requests")]
    public Task<IActionResult> Requests([FromQuery] string? state, CancellationToken token) => ExecuteAsync(token,
        (_, context) => promotions.ListRequestReceiptsAsync(context, state, token));

    [HttpGet("requests/{id:guid}")]
    public Task<IActionResult> GetRequest(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await promotions.RequestReceiptAsync(await promotions.GetRequestAsync(context, id, token), token));

    [HttpGet("requests/{id:guid}/preview")]
    public Task<IActionResult> RequestPreview(Guid id, CancellationToken token) => ExecuteAsync(token,
        (_, context) => promotions.PreviewRequestAsync(context, id, token));

    [HttpPost("requests/{id:guid}/approve")]
    public Task<IActionResult> ApproveRequest(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await promotions.RequestReceiptAsync(await promotions.ApproveRequestAsync(context, id, token), token), accepted: true);

    [HttpPost("requests/{id:guid}/decline")]
    public Task<IActionResult> DeclineRequest(Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PromotionRequestDecline? request,
        CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await promotions.RequestReceiptAsync(await promotions.DeclineRequestAsync(context, id, request?.Note, token), token));

    [HttpPost("requests/{id:guid}/cancel")]
    public Task<IActionResult> CancelRequest(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await promotions.RequestReceiptAsync(await promotions.CancelRequestAsync(context, id, token), token));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken token) => ExecuteAsync(token,
        (_, context) => promotions.GetReceiptAsync(context, id, token));

    [HttpPost("{id:guid}/approve")]
    public Task<IActionResult> Approve(Guid id, [FromBody] PromotionApproval request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await promotions.GetReceiptAsync(context, (await promotions.ApproveAsync(context, id, request.Reason, token)).Id, token), accepted: true);

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await promotions.GetReceiptAsync(context, (await promotions.CancelAsync(context, id, token)).Id, token));

    [HttpPost("{id:guid}/songs/{recordingId:guid}/review")]
    public Task<IActionResult> Review(Guid id, Guid recordingId, [FromBody] PromotionSongReview request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => await promotions.GetReceiptAsync(context, (await promotions.ReviewAsync(context, id, recordingId, request.Action, request.Reason, token)).Id, token), accepted: true);

    private async Task<object> StartReceiptAsync(ProtocolExecutionContext context, PromotionStartResult result, CancellationToken token)
    {
        if (result.Run is { } run && run.OwnerUserId != Guid.Empty)
            return await promotions.GetReceiptAsync(context, run.Id, token);
        return new { requested = true, request = await promotions.RequestReceiptAsync(result.Request!, token) };
    }

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
