using allstarr.Core.Favorites;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/favorite-events")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class FavoriteEventsController(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IFavoriteActionPipeline pipeline) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error)) return error!;
        limit = Math.Clamp(limit, 1, 500);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Set<FavoriteEventRecord>().AsNoTracking();
        if (!session.IsAdministrator)
        {
            if (session.TenantId is not { } tenantId || session.AllstarrUserId is not { } userId)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "The backend identity is not linked to an Allstarr user." });
            query = query.Where(item => item.TenantId == tenantId && item.OwnerUserId == userId);
        }
        var events = await query.OrderByDescending(item => item.CreatedAt).Take(limit)
            .Select(item => new
            {
                item.Id, item.JobId, operation = item.Operation.ToString(), state = item.State.ToString(),
                item.Protocol, item.BackendInstanceId, item.ItemId, item.CreatedAt, item.CompletedAt,
                item.LastErrorCode, item.LastErrorMessage
            }).ToListAsync(cancellationToken);
        return Ok(new { events });
    }

    [HttpGet("{eventId:guid}")]
    public async Task<IActionResult> Get(Guid eventId, CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var error)) return error!;
        if (!session.IsAdministrator)
        {
            if (session.TenantId is not { } tenantId || session.AllstarrUserId is not { } userId)
                return StatusCode(StatusCodes.Status403Forbidden);
            var status = await pipeline.GetStatusAsync(tenantId, userId, eventId, cancellationToken);
            return status == null ? NotFound() : Ok(status);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owner = await context.Set<FavoriteEventRecord>().AsNoTracking()
            .Where(item => item.Id == eventId)
            .Select(item => new { item.TenantId, item.OwnerUserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (owner == null) return NotFound();
        var administratorStatus = await pipeline.GetStatusAsync(
            owner.TenantId, owner.OwnerUserId, eventId, cancellationToken);
        return administratorStatus == null ? NotFound() : Ok(administratorStatus);
    }

    private bool TryGetSession(out AdminAuthSession session, out IActionResult? error)
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
}
