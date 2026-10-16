using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/storage/files")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ManagedStorageController(ManagedStorageRecovery recovery, IAntiforgery antiforgery) : ControllerBase
{
    private AdminAuthSession? Actor => HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] as AdminAuthSession;

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (Actor is not { IsAdministrator: true } actor) return StatusCode(403, new { error = "Administrator access required." });
        try { return Ok(new { settings = await recovery.GetStatusAsync(actor, token), csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken }); }
        catch (UnauthorizedAccessException error) { return StatusCode(403, new { error = error.Message }); }
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (Actor is not { IsAdministrator: true } actor) return StatusCode(403, new { error = "Administrator access required." });
        if (!await antiforgery.IsRequestValidAsync(HttpContext))
            return BadRequest(new { error = "Reload Storage before requesting verification.", code = "storage_csrf_required" });
        try
        {
            var jobId = await recovery.RequestVerificationAsync(actor, token);
            return Accepted(new { jobId, queued = jobId.HasValue, message = jobId.HasValue ? "Saved-file verification is queued. Follow progress in Activity." : "There are no saved files to verify." });
        }
        catch (UnauthorizedAccessException error) { return StatusCode(403, new { error = error.Message }); }
        catch (Exception error) when (DbErrors.IsTransientConflict(error) || DbErrors.IsUniqueViolation(error))
        { return Conflict(new { error = "Storage verification changed. Reload and try again." }); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return Conflict(new { error = "The data folder or its ownership marker needs attention. Check the mount and restore the complete folder before retrying.", code = "storage_marker_unavailable" }); }
    }
}
