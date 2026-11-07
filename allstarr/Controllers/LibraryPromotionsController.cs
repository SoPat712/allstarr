using System.Text.RegularExpressions;
using allstarr.Core.Enrichment;
using allstarr.Core.Protocols;
using allstarr.Core.Operations;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/library-promotions")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class LibraryPromotionsController(LibraryPromotionService promotions, PromotionRoots roots,
    AdminProtocolExecutionContextFactory contexts, ILogger<LibraryPromotionsController>? logger = null,
    PromotionLibraryFolders? libraryFolders = null) : ControllerBase
{
    [HttpGet("roots")]
    public Task<IActionResult> Roots(CancellationToken token) => ExecuteAsync(token, async (session, _) =>
        (await roots.ReadAsync(token)).Select(item => session.IsAdministrator
            ? (object)new { item.Id, item.Name, item.Path, item.BackendPath }
            : new { item.Id, item.Name }));

    [HttpGet("folders")]
    public Task<IActionResult> Folders(CancellationToken token) => ExecuteAsync(token, async (session, _) =>
    {
        if (!session.IsAdministrator) throw new UnauthorizedAccessException();
        return libraryFolders == null ? new PromotionLibraryFolderList(false, "backend-unavailable", [])
            : await libraryFolders.ReadAsync(session, token);
    });

    [HttpGet("setup")]
    public Task<IActionResult> Setup(CancellationToken token) => ExecuteAsync(token, (_, _) => roots.SetupAsync(token: token));

    [HttpPut("roots")]
    public Task<IActionResult> SaveRoots([FromBody] PromotionRoot[] request, CancellationToken token) => ExecuteAsync(token,
        async (session, _) =>
        {
            if (!session.IsAdministrator) throw new UnauthorizedAccessException();
            var approved = await roots.ReadAsync(token);
            var discovery = request.Length == 0 || libraryFolders == null
                ? new PromotionLibraryFolderDiscovery(false, "backend-unavailable", [])
                : await libraryFolders.DiscoverAsync(session, token);
            var sources = discovery.Folders.Select(folder => folder.BackendPath).ToHashSet(StringComparer.Ordinal);
            var verified = request.Select(root =>
            {
                var source = root.BackendPath ?? root.Path;
                if (sources.Contains(source)) return root with { BackendPath = source };
                if (approved.Any(item => PromotionRoots.SameLocation(item, root))) return root;
                throw new ArgumentException("Choose a discovered music folder before approving its local path.");
            }).ToArray();
            await roots.SaveAsync(session.AllstarrUserId!.Value, verified, token, sources);
            return new { saved = true };
        });

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
        catch (PromotionException error) { return StatusCode(error.Status, new { error = error.Message, reason = error.Reason }); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = new PromotionException(PromotionFailure.NotAuthorized).Message, reason = "not-authorized" }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { error = "The promotion request or root configuration is invalid.", reason = "invalid-request" }); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var redacted = Regex.Replace(error.Message, @"\b(?:Bearer|Basic)\s+\S+", "<redacted>", RegexOptions.IgnoreCase);
            var message = Regex.Replace(SafeOperationalText.Sanitize(redacted) ?? "Unexpected failure",
                """(["'])(?:[A-Za-z]:)?[\\/].*?\1|(?:https?://|(?:[A-Za-z]:)?[\\/])[^\s"'<>]+""", "<redacted-path>");
            logger?.LogError("Promotion failed: {ErrorType}: {Message}", error.GetType().Name, message);
            return StatusCode(500, new { error = "The promotion could not be completed. Try again or ask an administrator to check the server logs.", reason = "unexpected-error" });
        }
    }
}

public sealed record PromotionApproval(string Reason);
public sealed record PromotionSongReview(string Action, string? Reason = null);
public sealed record PromotionRequestDecline(string? Note = null);
