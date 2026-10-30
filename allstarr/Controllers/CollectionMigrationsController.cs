using allstarr.Core.Migrations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/migrations")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class CollectionMigrationsController(CollectionMigrationService migrations,
    AdminProtocolExecutionContextFactory contexts, IDbContextFactory<AllstarrDbContext> factory) : ControllerBase
{
    public sealed record StartRequest(Guid ProviderAccountId, string RequestKey);
    public sealed record ChooseRequest(IReadOnlyList<MigrationSelection> Collections);

    [HttpPost]
    public Task<IActionResult> Start([FromBody] StartRequest request, [FromQuery] Guid? targetUserId, CancellationToken token) =>
        ExecuteAsync(async execution =>
        {
            var id = await migrations.StartAsync(execution, request.ProviderAccountId, request.RequestKey, token);
            return Accepted($"/api/admin/migrations/{id}", new { id });
        }, targetUserId, token);

    [HttpGet("{id:guid}/discover")]
    public Task<IActionResult> Discover(Guid id, [FromQuery] string? cursor, [FromQuery] Guid? targetUserId, CancellationToken token) =>
        ExecuteAsync(async execution => Ok(await migrations.DiscoverAsync(execution, id, cursor, token)), targetUserId, token);

    [HttpGet("{id:guid}")]
    [HttpGet("{id:guid}/summary")]
    public Task<IActionResult> Status(Guid id, [FromQuery] Guid? targetUserId, CancellationToken token) =>
        ExecuteAsync(async execution => Ok(await migrations.StatusAsync(execution, id, token)), targetUserId, token);

    [HttpGet("{id:guid}/review")]
    public Task<IActionResult> Review(Guid id, [FromQuery] Guid collectionId, [FromQuery] int offset,
        [FromQuery] Guid? targetUserId, CancellationToken token, [FromQuery] int limit = 50) =>
        ExecuteAsync(async execution => Ok(await migrations.ReviewAsync(execution, id, collectionId, offset, limit, token)), targetUserId, token);

    [HttpPost("{id:guid}/choose")]
    public Task<IActionResult> Choose(Guid id, [FromBody] ChooseRequest request, [FromQuery] Guid? targetUserId, CancellationToken token) =>
        ExecuteAsync(async execution =>
        {
            await migrations.ChangeAsync(execution, id, "choose", request.Collections, token);
            return Accepted($"/api/admin/migrations/{id}");
        }, targetUserId, token);

    [HttpPost("{id:guid}/apply")]
    [HttpPost("{id:guid}/cancel")]
    [HttpPost("{id:guid}/resume")]
    public Task<IActionResult> Change(Guid id, [FromQuery] Guid? targetUserId, CancellationToken token) =>
        ExecuteAsync(async execution =>
        {
            var action = Request.Path.Value!.TrimEnd('/').Split('/')[^1];
            await migrations.ChangeAsync(execution, id, action, null, token);
            return Accepted($"/api/admin/migrations/{id}");
        }, targetUserId, token);

    private async Task<IActionResult> ExecuteAsync(Func<ProtocolExecutionContext, Task<IActionResult>> action, Guid? targetUserId, CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        try
        {
            var execution = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token);
            if (targetUserId.HasValue && targetUserId != session.AllstarrUserId)
            {
                if (!session.IsAdministrator) return Forbid();
                await using var db = await factory.CreateDbContextAsync(token);
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == targetUserId && item.Enabled, token)
                    ?? throw new UnauthorizedAccessException();
                execution = migrations.Execution(user, HttpContext.TraceIdentifier, token);
            }
            return await action(execution);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { error = "Check the selected collections and request key." }); }
        catch (InvalidOperationException) { return Conflict(new { error = "The run or connection changed. Read its status before continuing." }); }
    }
}
