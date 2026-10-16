using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/jobs")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class JobsController(IDbContextFactory<AllstarrDbContext> contextFactory, DurableJobQueue queue) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? state = null, [FromQuery] string? type = null,
        [FromQuery] int limit = 100, CancellationToken cancellationToken = default,
        [FromQuery] string? view = null, [FromQuery] DateTimeOffset? before = null, [FromQuery] Guid? beforeId = null,
        [FromQuery] string scope = "mine")
    {
        if (!TryGetSession(out var session)) return Unauthorized(new { error = "Authentication required" });
        if (!session.AllstarrUserId.HasValue) return Forbid();
        if (scope is not ("mine" or "everyone")) return BadRequest(new { error = "Unknown activity scope" });
        if (scope == "everyone" && !session.IsAdministrator) return Forbid();
        var everyone = session.IsAdministrator && scope == "everyone";
        if (view is not (null or "queue" or "history")) return BadRequest(new { error = "Unknown job view" });
        if (before.HasValue != beforeId.HasValue) return BadRequest(new { error = "A complete page cursor is required" });
        limit = Math.Clamp(limit, 1, 500);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Jobs.AsNoTracking().Where(x => everyone || x.OwnerUserId == session.AllstarrUserId);
        if (view == "queue") query = query.Where(x => x.State == DurableJobState.Pending || x.State == DurableJobState.Running || x.State == DurableJobState.RetryScheduled);
        if (view == "history") query = query.Where(x => x.State == DurableJobState.Succeeded || x.State == DurableJobState.Failed || x.State == DurableJobState.Cancelled);
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (string.Equals(state, "Deferred", StringComparison.OrdinalIgnoreCase))
                query = query.Where(x => x.State == DurableJobState.RetryScheduled && x.LastAttemptOutcome == "deferred");
            else if (Enum.TryParse<DurableJobState>(state, true, out var parsed) && Enum.IsDefined(parsed))
                query = query.Where(x => x.State == parsed && (parsed != DurableJobState.RetryScheduled || x.LastAttemptOutcome != "deferred"));
            else return BadRequest(new { error = "Unknown job state" });
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            var normalized = type.Trim().ToLowerInvariant();
            query = query.Where(x => x.Type == normalized);
        }
        var history = view == "history";
        if (before.HasValue)
            query = query.Where(x => (history ? x.CompletedAt ?? x.CreatedAt : x.CreatedAt) < before.Value ||
                (history ? x.CompletedAt ?? x.CreatedAt : x.CreatedAt) == before.Value && x.Id.CompareTo(beforeId!.Value) < 0);
        var rows = await query.OrderByDescending(x => history ? x.CompletedAt ?? x.CreatedAt : x.CreatedAt)
            .ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        var owners = rows.Select(x => x.OwnerUserId).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(x => owners.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => new { x.DisplayName, x.BackendType }, cancellationToken);
        var ids = rows.Select(x => x.Id.ToString("D")).ToArray();
        var correlations = rows.Select(x => x.CorrelationId).Distinct().ToArray();
        var progressRows = await db.AuditEvents.AsNoTracking().Where(x => x.Category == "job-progress" &&
                (everyone || x.ActorUserId == session.AllstarrUserId) &&
                (x.SubjectType == "job" && ids.Contains(x.SubjectId) || x.SubjectId == null && correlations.Contains(x.CorrelationId)))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(Math.Min(2500, limit * 25)).ToArrayAsync(cancellationToken);
        var legacyCounts = await db.Jobs.AsNoTracking().Where(x => correlations.Contains(x.CorrelationId) &&
                (everyone || x.OwnerUserId == session.AllstarrUserId))
            .GroupBy(x => new { x.OwnerUserId, x.CorrelationId })
            .Select(group => new { group.Key.OwnerUserId, group.Key.CorrelationId, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        var last = rows.LastOrDefault();
        return Ok(new
        {
            jobs = rows.Select(x => Present(x, x.OwnerUserId is { } owner ? ActivityPresentation.UserName(names.GetValueOrDefault(owner)?.DisplayName) : "Household",
                x.OwnerUserId is { } user ? names.GetValueOrDefault(user)?.BackendType : null)),
            progress = progressRows.Select(x => (Event: x, Job: rows.SingleOrDefault(job => job.OwnerUserId == x.ActorUserId &&
                (x.SubjectId != null ? x.SubjectType == "job" && x.SubjectId == job.Id.ToString("D") :
                    job.CorrelationId == x.CorrelationId && legacyCounts.Any(count => count.OwnerUserId == x.ActorUserId && count.CorrelationId == x.CorrelationId && count.Count == 1)))))
                .Where(x => x.Job != null).Select(x => new
                {
                    x.Event.Id,
                    jobId = x.Job!.Id,
                    x.Event.Action,
                    x.Event.Outcome,
                    DetailsJson = JobProgressPresentation.SafeDetails(x.Event.DetailsJson),
                    x.Event.CreatedAt
                }),
            hasMore,
            nextCursor = hasMore && last != null ? (DateTimeOffset?)(history ? last.CompletedAt ?? last.CreatedAt : last.CreatedAt) : null,
            nextCursorId = hasMore ? last?.Id : null
        });
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> Get(Guid jobId, CancellationToken cancellationToken = default,
        [FromQuery] int limit = 50, [FromQuery] DateTimeOffset? before = null, [FromQuery] Guid? beforeId = null)
    {
        if (!TryGetSession(out var session)) return Unauthorized(new { error = "Authentication required" });
        if (before.HasValue != beforeId.HasValue) return BadRequest(new { error = "A complete page cursor is required" });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId &&
            (session.IsAdministrator || session.AllstarrUserId.HasValue && x.OwnerUserId == session.AllstarrUserId), cancellationToken);
        if (job == null) return NotFound();
        limit = Math.Clamp(limit, 1, 100);
        var subject = job.Id.ToString("D");
        var events = db.AuditEvents.AsNoTracking().Where(x => x.Category == "job-attempt" && x.SubjectType == "job" &&
            x.SubjectId == subject && x.ActorUserId == job.OwnerUserId);
        if (before.HasValue) events = events.Where(x => x.CreatedAt < before.Value || x.CreatedAt == before.Value && x.Id.CompareTo(beforeId!.Value) < 0);
        var history = await events.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToArrayAsync(cancellationToken);
        var page = history.Take(limit).ToArray();
        var hasMore = history.Length > limit;
        var progress = await db.AuditEvents.AsNoTracking().Where(x => x.Category == "job-progress" && x.ActorUserId == job.OwnerUserId &&
                (x.SubjectType == "job" && x.SubjectId == subject || x.SubjectId == null && x.CorrelationId == job.CorrelationId &&
                    !db.Jobs.Any(other => other.Id != job.Id && other.OwnerUserId == job.OwnerUserId && other.CorrelationId == job.CorrelationId)))
            .OrderByDescending(x => x.CreatedAt).Take(100).ToArrayAsync(cancellationToken);
        return Ok(new
        {
            job = Present(job, null),
            attempts = page.Select(x => DurableJobHistory.Read(x, jobId)).OfType<DurableJobAttemptHistory>().OrderByDescending(x => x.AttemptNumber),
            progress = progress.Select(x => new { x.Id, x.Action, x.Outcome, DetailsJson = JobProgressPresentation.SafeDetails(x.DetailsJson), x.CreatedAt }),
            hasMore,
            nextCursor = hasMore ? page.LastOrDefault()?.CreatedAt : null,
            nextCursorId = hasMore ? page.LastOrDefault()?.Id : null
        });
    }

    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session)) return Unauthorized(new { error = "Authentication required" });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId &&
            (session.IsAdministrator || session.AllstarrUserId.HasValue && x.OwnerUserId == session.AllstarrUserId), cancellationToken);
        if (job == null) return NotFound();
        return await queue.RequestCancellationAsync(job.Id, job.OwnerUserId, cancellationToken)
            ? Accepted(new { jobId, state = "cancellation_requested" })
            : Conflict(new { error = "Job is missing or already terminal" });
    }

    private static object Present(DurableJobRecord job, string? ownerName, string? backend = null) => new
    {
        job.Id,
        job.CorrelationId,
        job.OwnerUserId,
        ownerName,
        source = ActivityPresentation.JobSource(job.Type, backend),
        job.Type,
        state = job.State == DurableJobState.RetryScheduled && job.LastAttemptOutcome == "deferred" ? "Deferred" : job.State.ToString(),
        canCancel = job.CancellationRequestedAt == null && job.State is DurableJobState.Pending or DurableJobState.Running or DurableJobState.RetryScheduled,
        job.Priority,
        job.AttemptCount,
        job.FailureCount,
        job.DeferralCount,
        job.MaxAttempts,
        job.MaxDeferrals,
        job.AvailableAt,
        job.CancellationRequestedAt,
        job.StartedAt,
        job.LastAttemptStartedAt,
        job.LastAttemptCompletedAt,
        job.LastAttemptOutcome,
        job.CompletedAt,
        LastErrorCode = SafeOperationalText.Sanitize(job.LastErrorCode, 100),
        LastErrorMessage = SafeOperationalText.Sanitize(job.LastErrorMessage, 1000),
        job.CreatedAt,
        job.UpdatedAt
    };

    private bool TryGetSession(out AdminAuthSession session)
    {
        session = HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] as AdminAuthSession ?? null!;
        return session != null;
    }
}
