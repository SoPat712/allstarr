using allstarr.Core.Jobs;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
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
    DurableJobQueue jobs,
    IBackendLibraryAccessResolver libraryAccess) : ControllerBase
{
    [HttpPost("enqueue")]
    public async Task<IActionResult> Enqueue([FromBody] EnqueueLibraryIndexRequest request, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        if (request.PageSize is < 1 or > 500)
            return BadRequest(new { error = "PageSize must be between 1 and 500" });
        var backendLibraryId = NormalizeBackendLibrary(request.BackendLibraryId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var backendType = session!.BackendType.Trim().ToLowerInvariant();
        var identity = await db.Users.AsNoTracking().Where(item => item.Enabled && item.IsAdmin &&
            item.Id == session.AllstarrUserId && item.BackendType == backendType &&
            item.BackendInstanceId == session.BackendInstanceId && item.BackendPrincipalId == session.UserId)
            .OrderByDescending(item => item.LastSeenAt).FirstOrDefaultAsync(cancellationToken);
        if (identity == null) return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        if (backendType is "subsonic" or "navidrome" or "opensubsonic")
        {
            if (!request.CredentialReferenceId.HasValue) return BadRequest(new { error = "Subsonic indexing requires CredentialReferenceId" });
            var valid = await db.SecretReferences.AsNoTracking().AnyAsync(item => item.Id == request.CredentialReferenceId &&
                item.UserId == identity.Id &&
                item.RevokedAt == null && item.Purpose == BackendCredentialScope.SubsonicPurpose, cancellationToken);
            if (!valid) return BadRequest(new { error = "CredentialReferenceId is unavailable for this user" });
        }
        var generation = request.Generation ?? DateTimeOffset.UtcNow.UtcTicks;
        var libraryKey = backendLibraryId == null ? "all" : $"library:{backendLibraryId}";
        var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<LibraryIndexJobPayload>(
            "library.index", $"library-index:{session.AllstarrUserId:N}:{identity.BackendInstanceId}:{libraryKey}:{generation}",
            new(backendLibraryId, identity.BackendInstanceId, identity.BackendPrincipalId, request.CredentialReferenceId, request.PageSize),
            session.AllstarrUserId,
            CorrelationId: HttpContext.TraceIdentifier), cancellationToken);
        return Accepted(new { jobId = result.JobId, created = result.Created, generation });
    }

    [HttpGet("counts")]
    public async Task<IActionResult> Counts([FromQuery] string? backendLibraryId, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        backendLibraryId = NormalizeBackendLibrary(backendLibraryId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var backendType = session!.BackendType.Trim().ToLowerInvariant();
        var identity = await db.Users.AsNoTracking().Where(item => item.Enabled && item.IsAdmin &&
            item.Id == session.AllstarrUserId && item.BackendType == backendType &&
            item.BackendInstanceId == session.BackendInstanceId && item.BackendPrincipalId == session.UserId)
            .OrderByDescending(item => item.LastSeenAt).FirstOrDefaultAsync(cancellationToken);
        if (identity == null) return StatusCode(403, new { error = "The linked backend identity is unavailable" });
        var access = await libraryAccess.ResolveUserAsync(session.AllstarrUserId!.Value, cancellationToken);
        var protocol = backendType == "jellyfin" ? "jellyfin" : "subsonic";
        var tracks = LibraryTrackAccess.Query(db, access).Where(item => item.BackendInstanceId == identity.BackendInstanceId && item.Protocol == protocol);
        if (backendLibraryId != null) tracks = tracks.Where(item => item.BackendLibraryId == backendLibraryId);
        var count = await tracks.Select(item => item.BackendItemId).Distinct().CountAsync(cancellationToken);
        var lastIndexedAt = await tracks.MaxAsync(item => (DateTimeOffset?)item.IndexedAt, cancellationToken);
        var recentScans = await db.AuditEvents.AsNoTracking().Where(item => item.ActorUserId == session.AllstarrUserId && item.Category == "library-index" &&
                item.Action == "scan.completed")
            .OrderByDescending(item => item.CreatedAt).Take(100).ToListAsync(cancellationToken);
        object? scan = null;
        foreach (var latestScan in recentScans)
        {
            using var details = JsonDocument.Parse(latestScan.DetailsJson);
            var root = details.RootElement;
            if (root.TryGetProperty("BackendLibraryId", out var scope) && scope.GetString() == backendLibraryId &&
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
        return Ok(new { backendLibraryId, backendInstanceId = identity.BackendInstanceId, trackCount = count, lastIndexedAt, latestScan = scan });
    }

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null; error = null;
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) || value is not AdminAuthSession authenticated)
        { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (!authenticated.IsAdministrator)
        { error = StatusCode(403, new { error = "Administrator access required" }); return false; }
        if (!authenticated.AllstarrUserId.HasValue)
        { error = StatusCode(403, new { error = "The backend identity is not linked to an Allstarr user" }); return false; }
        session = authenticated; return true;
    }

    private static string? NormalizeBackendLibrary(string? backendLibraryId) =>
        string.IsNullOrWhiteSpace(backendLibraryId) ? null : backendLibraryId.Trim();
}

public sealed record EnqueueLibraryIndexRequest(
    string? BackendLibraryId = null,
    Guid? CredentialReferenceId = null,
    int PageSize = 200,
    long? Generation = null);
