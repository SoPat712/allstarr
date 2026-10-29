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
        async (_, context) => Receipt(await promotions.StartAsync(context, request, token)), accepted: true);

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => Receipt(await promotions.GetAsync(context, id, token)));

    [HttpPost("{id:guid}/approve")]
    public Task<IActionResult> Approve(Guid id, [FromBody] PromotionApproval request, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => Receipt(await promotions.ApproveAsync(context, id, request.Reason, token)), accepted: true);

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken token) => ExecuteAsync(token,
        async (_, context) => Receipt(await promotions.CancelAsync(context, id, token)));

    private static object Receipt(Core.Storage.PromotionRunRecord run) => new
    { run.Id, run.JobId, run.Step, run.Outcome, run.Reason, run.ManagedFileId, run.ApprovedByUserId, run.ApprovalReason };

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
