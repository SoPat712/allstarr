using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/managed-files")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ManagedFilesController(
    IDbContextFactory<AllstarrDbContext> factory,
    ManagedFileRemovalService removal) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        limit = Math.Clamp(limit, 1, 500);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.ManagedFiles.AsNoTracking().Where(item => item.RemovedAt == null);
        if (!session.IsAdministrator)
        {
            if (session.TenantId is not { } tenantId || session.AllstarrUserId is not { } userId)
                return StatusCode(StatusCodes.Status403Forbidden);
            query = query.Where(item => item.TenantId == tenantId && item.OwnerUserId == userId);
        }
        var items = await query.OrderByDescending(item => item.CreatedAt).Take(limit).ToListAsync(cancellationToken);
        return Ok(new { files = items.Select(SafeResponse) });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(
        Guid id, [FromBody] ManagedFileRemovalRequest request, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (request is null || !request.ExplicitlyConfirmed)
            return BadRequest(new { error = "managed_file_confirmation_required" });
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.ManagedFiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken);
        if (record == null) return NotFound();
        if (!session.IsAdministrator &&
            (session.TenantId != record.TenantId || session.AllstarrUserId != record.OwnerUserId))
            return NotFound();
        try
        {
            await removal.RemoveAsync(id, record.ScopeKey, explicitlyConfirmed: true, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(StatusCodes.Status403Forbidden); }
        catch (InvalidOperationException) { return Conflict(new { error = "managed_file_has_protected_references" }); }
    }

    private bool TrySession(out AdminAuthSession session, out IActionResult? error)
    {
        session = null!;
        error = null;
        if (HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) &&
            value is AdminAuthSession current)
        {
            session = current;
            return true;
        }
        error = Unauthorized(new { error = "Authentication required" });
        return false;
    }

    private static object SafeResponse(ManagedFileOwnershipEntity item) => new
    {
        item.Id, item.RootId, item.OwnerUserId, item.LibraryScopeId, item.Length,
        placementMethod = item.PlacementMethod.ToString(), item.ReferenceCount, item.CreatedAt,
        fileName = Path.GetFileName(item.CanonicalPath)
    };
}

public sealed class ManagedFileRemovalRequest
{
    public bool ExplicitlyConfirmed { get; set; }
}
