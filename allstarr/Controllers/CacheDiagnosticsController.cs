using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Core.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/cache")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class CacheDiagnosticsController(
    HybridApplicationCache cache,
    ExtensionRuntimeCoordinator? extensions = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error)
        {
            return error;
        }

        return Ok((await cache.GetDiagnosticsAsync(cancellationToken)) with
        {
            ExtensionStorage = extensions?.GetStorageUsage() ?? new(0, 0, 0, 0)
        });
    }

    [HttpDelete("{scope}")]
    public async Task<IActionResult> Purge(string scope)
    {
        if (RequireAdministrator() is { } error)
        {
            return error;
        }

        var normalized = scope.Trim().ToLowerInvariant();
        var deleted = normalized switch
        {
            "metadata" => await cache.PurgeMetadataAsync(),
            "media" => await cache.PurgeMediaAsync(),
            "all" => await cache.PurgeAllAsync(),
            _ => -1
        };
        return deleted < 0
            ? BadRequest(new { error = "cache_purge_scope_invalid", allowed = new[] { "metadata", "media", "all" } })
            : Ok(new { scope = normalized, deleted });
    }

    [HttpGet("maintenance/preview")]
    public async Task<IActionResult> PreviewMaintenance(
        CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error)
        {
            return error;
        }

        return Ok(await cache.PreviewMaintenanceAsync(cancellationToken));
    }

    [HttpPost("maintenance")]
    public async Task<IActionResult> RunMaintenance(
        CancellationToken cancellationToken = default)
    {
        if (RequireAdministrator() is { } error)
        {
            return error;
        }

        var before = await cache.PreviewMaintenanceAsync(cancellationToken);
        var deleted = await cache.CleanupAsync(cancellationToken);
        var after = await cache.PreviewMaintenanceAsync(cancellationToken);
        return Ok(new { deleted, before, after });
    }

    private IActionResult? RequireAdministrator()
    {
        if (!HttpContext.Items.TryGetValue(
                AdminAuthSessionService.HttpContextSessionItemKey,
                out var value) ||
            value is not AdminAuthSession session)
        {
            return Unauthorized(new { error = "admin_session_required" });
        }

        return session.IsAdministrator
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { error = "administrator_required" });
    }
}
