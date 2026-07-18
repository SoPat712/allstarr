using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/library-index")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class LibraryIndexController(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableJobQueue jobs) : ControllerBase
{
    [HttpPost("enqueue")]
    public async Task<IActionResult> Enqueue([FromBody] EnqueueLibraryIndexRequest request, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (string.IsNullOrWhiteSpace(request.LibraryScopeId) || request.PageSize is < 1 or > 500)
            return BadRequest(new { error = "LibraryScopeId is required and PageSize must be between 1 and 500" });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var backendType = session!.BackendType.Trim().ToLowerInvariant();
        var identity = await db.BackendIdentities.AsNoTracking().Where(item => item.TenantId == session.TenantId &&
            item.UserId == session.AllstarrUserId && item.BackendType == backendType && item.PrincipalId == session.UserId)
            .OrderByDescending(item => item.LastSeenAt).FirstOrDefaultAsync(cancellationToken);
        if (identity == null) return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        if (backendType is "subsonic" or "navidrome" or "opensubsonic")
        {
            if (!request.CredentialReferenceId.HasValue) return BadRequest(new { error = "Subsonic indexing requires CredentialReferenceId" });
            var valid = await db.SecretReferences.AsNoTracking().AnyAsync(item => item.Id == request.CredentialReferenceId &&
                item.TenantId == session.TenantId && item.RevokedAt == null && item.Purpose == "playlist-backend:subsonic", cancellationToken);
            if (!valid) return BadRequest(new { error = "CredentialReferenceId is unavailable in this tenant" });
        }
        var generation = request.Generation ?? DateTimeOffset.UtcNow.UtcTicks;
        var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<LibraryIndexJobPayload>(
            "library.index", $"library-index:{session.TenantId:N}:{session.AllstarrUserId:N}:{identity.BackendInstanceId}:{request.LibraryScopeId}:{generation}",
            new(request.LibraryScopeId.Trim(), identity.BackendInstanceId, identity.PrincipalId, request.CredentialReferenceId, request.PageSize),
            session.TenantId, session.AllstarrUserId, LibraryScopeId: request.LibraryScopeId.Trim(),
            CorrelationId: HttpContext.TraceIdentifier), cancellationToken);
        return Accepted(new { jobId = result.JobId, created = result.Created, generation });
    }

    [HttpGet("counts")]
    public async Task<IActionResult> Counts([FromQuery] string libraryScopeId, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (string.IsNullOrWhiteSpace(libraryScopeId)) return BadRequest(new { error = "LibraryScopeId is required" });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var backendType = session!.BackendType.Trim().ToLowerInvariant();
        var identity = await db.BackendIdentities.AsNoTracking().Where(item => item.TenantId == session.TenantId &&
            item.UserId == session.AllstarrUserId && item.BackendType == backendType && item.PrincipalId == session.UserId)
            .OrderByDescending(item => item.LastSeenAt).FirstOrDefaultAsync(cancellationToken);
        if (identity == null) return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        var count = await db.LibraryTracks.AsNoTracking().CountAsync(item => item.TenantId == session.TenantId &&
            item.OwnerUserId == session.AllstarrUserId && item.BackendInstanceId == identity.BackendInstanceId &&
            item.LibraryScopeId == libraryScopeId, cancellationToken);
        var lastIndexedAt = await db.LibraryTracks.AsNoTracking().Where(item => item.TenantId == session.TenantId &&
            item.OwnerUserId == session.AllstarrUserId && item.BackendInstanceId == identity.BackendInstanceId &&
            item.LibraryScopeId == libraryScopeId).MaxAsync(item => (DateTimeOffset?)item.IndexedAt, cancellationToken);
        var recentScans = await db.AuditEvents.AsNoTracking().Where(item => item.TenantId == session.TenantId &&
                item.ActorUserId == session.AllstarrUserId && item.Category == "library-index" &&
                item.Action == "scan.completed")
            .OrderByDescending(item => item.CreatedAt).Take(100).ToListAsync(cancellationToken);
        object? scan = null;
        foreach (var latestScan in recentScans)
        {
            using var details = JsonDocument.Parse(latestScan.DetailsJson);
            var root = details.RootElement;
            if (root.TryGetProperty("LibraryScopeId", out var scope) && scope.GetString() == libraryScopeId &&
                root.TryGetProperty("BackendInstanceId", out var backend) && backend.GetString() == identity.BackendInstanceId)
            {
                scan = new
                {
                    seen = root.GetProperty("Seen").GetInt32(),
                    indexed = root.GetProperty("Indexed").GetInt32(),
                    skippedPathless = root.GetProperty("SkippedPathless").GetInt32(),
                    skippedMalformed = root.GetProperty("SkippedMalformed").GetInt32(),
                    pages = root.GetProperty("Pages").GetInt32(),
                    completedAt = latestScan.CreatedAt
                };
                break;
            }
        }
        return Ok(new { libraryScopeId, backendInstanceId = identity.BackendInstanceId, trackCount = count, lastIndexedAt, latestScan = scan });
    }

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null; error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession authenticated)
        { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (!authenticated.TenantId.HasValue || !authenticated.AllstarrUserId.HasValue)
        { error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" }); return false; }
        session = authenticated; return true;
    }
}

public sealed record EnqueueLibraryIndexRequest(
    string LibraryScopeId,
    Guid? CredentialReferenceId = null,
    int PageSize = 200,
    long? Generation = null);
