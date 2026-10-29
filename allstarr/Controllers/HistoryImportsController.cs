using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/history/imports")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class HistoryImportsController(
    ListeningHistoryImportService imports,
    IDbContextFactory<AllstarrDbContext> factory,
    IPlatformClock clock,
    ListeningHistoryImportOptions options) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(ListeningHistoryImportOptions.UploadLimitBytes)]
    public Task<IActionResult> Upload([FromQuery] string fileName, CancellationToken token) => ExecuteAsync(async scope =>
    {
        if (Request.ContentLength is not { } length) return StatusCode(411, new { error = "A file length is required." });
        if (length > Math.Min(options.MaximumUploadBytes, ListeningHistoryImportOptions.UploadLimitBytes))
            return StatusCode(413, new { error = "The history upload exceeds the size limit." });
        return Ok(await imports.PreviewAsync(scope, fileName, Request.Body, length, token));
    }, token);

    [HttpGet]
    public Task<IActionResult> List(CancellationToken token) => ExecuteAsync(async scope =>
        Ok(await imports.ListAsync(scope, 50, token)), token);

    [HttpGet("{importId:guid}")]
    public Task<IActionResult> Get(Guid importId, CancellationToken token) => ExecuteAsync(async scope =>
        await imports.GetAsync(scope, importId, token) is { } result ? Ok(result) : NotFound(), token);

    [HttpPost("{importId:guid}/confirm")]
    public Task<IActionResult> Confirm(Guid importId, [FromBody] HistoryImportConfirmation request, CancellationToken token) => ExecuteAsync(async scope =>
        await imports.ApplyAsync(scope, importId, request.Revision, token) is { } result ? Ok(result) : NotFound(), token);

    [HttpDelete("{importId:guid}")]
    public Task<IActionResult> Delete(Guid importId, [FromQuery] string revision, CancellationToken token) => ExecuteAsync(async scope =>
        await imports.RemoveAsync(scope, importId, revision, token) is { } result ? Ok(result) : NotFound(), token);

    private async Task<IActionResult> ExecuteAsync(Func<IntelligenceScope, Task<IActionResult>> action, CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (session.AllstarrUserId is not { } user || session.ExpiresAtUtc <= clock.UtcNow.UtcDateTime)
            return StatusCode(403, new { error = "A linked backend listener is required." });
        var protocol = session.BackendType.ToLowerInvariant();
        if (protocol is not ("jellyfin" or "subsonic")) return Unauthorized();
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Users.AnyAsync(item => item.Id == user && item.Enabled && item.BackendType == protocol &&
                item.BackendInstanceId == session.BackendInstanceId && item.BackendPrincipalId == session.UserId &&
                item.IsAdmin == session.IsAdministrator, token))
            return StatusCode(403, new { error = "This listener is unavailable." });
        try { return await action(new(user, protocol, session.BackendInstanceId)); }
        catch (ListeningHistoryImportException error)
        {
            return error.Code.Contains("conflict", StringComparison.Ordinal)
                ? Conflict(new { error = error.Code, message = error.Message })
                : BadRequest(new { error = error.Code, message = error.Message });
        }
        catch (InvalidDataException) { return BadRequest(new { error = "The history file is invalid or exceeds the size limit." }); }
        catch (ArgumentException) { return BadRequest(new { error = "The history request is invalid." }); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "This listener is unavailable." }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "The import changed. Refresh and try again." }); }
    }
}

public sealed record HistoryImportConfirmation(string Revision);
