using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/jobs")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class JobsController : ControllerBase
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableJobQueue _queue;

    public JobsController(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableJobQueue queue)
    {
        _contextFactory = contextFactory;
        _queue = queue;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? state = null,
        [FromQuery] string? type = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var sessionError))
        {
            return sessionError!;
        }

        limit = Math.Clamp(limit, 1, 500);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Jobs.AsNoTracking();
        if (!session.IsAdministrator)
        {
            if (!session.TenantId.HasValue || !session.AllstarrUserId.HasValue)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "The backend identity is not linked to an Allstarr user."
                });
            }

            query = query.Where(item =>
                item.TenantId == session.TenantId &&
                item.OwnerUserId == session.AllstarrUserId);
        }
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<DurableJobState>(state, ignoreCase: true, out var parsedState))
            {
                return BadRequest(new { error = "Unknown job state" });
            }

            query = query.Where(item => item.State == parsedState);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            var normalizedType = type.Trim().ToLowerInvariant();
            query = query.Where(item => item.Type == normalizedType);
        }

        var jobs = await query
            .OrderByDescending(item => item.CreatedAt)
            .Take(limit)
            .Select(item => new
            {
                item.Id,
                item.CorrelationId,
                item.TenantId,
                item.OwnerUserId,
                item.Type,
                state = item.State.ToString(),
                item.Priority,
                item.AttemptCount,
                item.FailureCount,
                item.DeferralCount,
                item.MaxAttempts,
                item.MaxDeferrals,
                item.AvailableAt,
                item.CancellationRequestedAt,
                item.StartedAt,
                item.CompletedAt,
                item.LastErrorCode,
                item.LastErrorMessage,
                item.CreatedAt,
                item.UpdatedAt
            })
            .ToListAsync(cancellationToken);
        var correlationIds = jobs.Select(item => item.CorrelationId).Distinct().ToArray();
        var jobByCorrelation = jobs
            .GroupBy(item => item.CorrelationId)
            .ToDictionary(group => group.Key, group => group.First().Id);
        var progress = await context.AuditEvents.AsNoTracking()
            .Where(item => item.Category == "job-progress" &&
                           correlationIds.Contains(item.CorrelationId))
            .OrderByDescending(item => item.CreatedAt)
            .Take(Math.Min(2500, limit * 25))
            .Select(item => new
            {
                item.Id,
                item.CorrelationId,
                item.Action,
                item.Outcome,
                item.DetailsJson,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            jobs,
            progress = progress.Select(item => new
            {
                item.Id,
                jobId = jobByCorrelation.GetValueOrDefault(item.CorrelationId),
                item.Action,
                item.Outcome,
                item.DetailsJson,
                item.CreatedAt
            })
        });
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> Get(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var sessionError))
        {
            return sessionError!;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Jobs.AsNoTracking().Where(item => item.Id == jobId);
        if (!session.IsAdministrator)
        {
            query = query.Where(item =>
                session.TenantId.HasValue &&
                session.AllstarrUserId.HasValue &&
                item.TenantId == session.TenantId &&
                item.OwnerUserId == session.AllstarrUserId);
        }

        var job = await query.SingleOrDefaultAsync(cancellationToken);
        if (job == null)
        {
            return NotFound();
        }

        var attempts = await context.JobAttempts.AsNoTracking()
            .Where(item => item.JobId == jobId)
            .OrderBy(item => item.AttemptNumber)
            .Select(item => new
            {
                item.AttemptNumber,
                item.WorkerId,
                item.StartedAt,
                item.CompletedAt,
                item.Outcome,
                item.ErrorCode,
                item.ErrorMessage
            })
            .ToListAsync(cancellationToken);
        var progress = await context.AuditEvents.AsNoTracking()
            .Where(item => item.Category == "job-progress" &&
                           item.CorrelationId == job.CorrelationId)
            .OrderByDescending(item => item.CreatedAt)
            .Take(100)
            .Select(item => new
            {
                item.Id,
                item.Action,
                item.Outcome,
                item.DetailsJson,
                item.CreatedAt
            })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            job = new
            {
                job.Id,
                job.TenantId,
                job.OwnerUserId,
                job.Type,
                state = job.State.ToString(),
                job.AttemptCount,
                job.FailureCount,
                job.DeferralCount,
                job.MaxAttempts,
                job.MaxDeferrals,
                job.AvailableAt,
                job.CancellationRequestedAt,
                job.StartedAt,
                job.CompletedAt,
                job.LastErrorCode,
                job.LastErrorMessage,
                job.CreatedAt,
                job.UpdatedAt
            },
            attempts,
            progress
        });
    }

    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out var session, out var sessionError))
        {
            return sessionError!;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Jobs.AsNoTracking().Where(item => item.Id == jobId);
        if (!session.IsAdministrator)
        {
            query = query.Where(item =>
                session.TenantId.HasValue &&
                session.AllstarrUserId.HasValue &&
                item.TenantId == session.TenantId &&
                item.OwnerUserId == session.AllstarrUserId);
        }

        var job = await query.Select(item => new { item.Id, item.TenantId })
            .SingleOrDefaultAsync(cancellationToken);
        if (job == null)
        {
            return NotFound();
        }

        var requested = await _queue.RequestCancellationAsync(job.Id, job.TenantId, cancellationToken);
        return requested
            ? Accepted(new { jobId, state = "cancellation_requested" })
            : Conflict(new { error = "Job is missing or already terminal" });
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
