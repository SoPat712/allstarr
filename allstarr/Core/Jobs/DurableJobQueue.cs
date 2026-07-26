using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed record DurableJobEnqueueRequest<T>(
    string Type,
    string IdempotencyKey,
    T Payload,
    Guid? TenantId = null,
    Guid? OwnerUserId = null,
    int Priority = 0,
    int? MaxAttempts = null,
    DateTimeOffset? AvailableAt = null,
    int? MaxDeferrals = null,
    Guid? ProviderAccountId = null,
    string? LibraryScopeId = null,
    string? Capability = null,
    string? CorrelationId = null);

public sealed record DurableJobEnqueueResult(Guid JobId, bool Created);

public sealed record DurableJobClaim(
    Guid JobId,
    Guid AttemptId,
    int AttemptNumber,
    string Type,
    JsonElement Payload,
    Guid? TenantId,
    Guid? OwnerUserId,
    Guid? ProviderAccountId,
    string? LibraryScopeId,
    string? ProviderCapability,
    JsonElement PolicySnapshot,
    string CorrelationId,
    string WorkerId,
    DateTimeOffset LeaseExpiresAt);

public enum DurableJobCompletionKind
{
    Succeeded,
    Retry,
    Failed,
    Deferred,
    Cancelled
}

public sealed record DurableJobCompletion(
    DurableJobCompletionKind Kind,
    string? ErrorCode = null,
    string? SafeMessage = null,
    TimeSpan? RetryDelay = null)
{
    public static DurableJobCompletion Success() => new(DurableJobCompletionKind.Succeeded);
    public static DurableJobCompletion Retry(string code, string? message, TimeSpan? delay = null) =>
        new(DurableJobCompletionKind.Retry, code, message, delay);
    public static DurableJobCompletion Failure(string code, string? message) =>
        new(DurableJobCompletionKind.Failed, code, message);
    public static DurableJobCompletion Defer(string code, string? message, TimeSpan? delay = null) =>
        new(DurableJobCompletionKind.Deferred, code, message, delay);
    public static DurableJobCompletion Cancelled() => new(DurableJobCompletionKind.Cancelled);
}

public sealed record DurableJobProgressUpdate(
    string Stage,
    string Message,
    int? Completed = null,
    int? Total = null,
    string? Provider = null,
    string? Playlist = null,
    string? Track = null,
    string? DeferralReason = null,
    double? ThroughputPerSecond = null);

public sealed class DurableJobQueue
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableJobOptions _options;
    private readonly JobPayloadPolicy _payloadPolicy;
    private readonly IPlatformClock _clock;
    private readonly DurableJobContextAuthorizer _contextAuthorizer;

    public DurableJobQueue(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableJobOptions options,
        JobPayloadPolicy payloadPolicy,
        IPlatformClock clock,
        DurableJobContextAuthorizer? contextAuthorizer = null)
    {
        _contextFactory = contextFactory;
        _options = options;
        _payloadPolicy = payloadPolicy;
        _clock = clock;
        _contextAuthorizer = contextAuthorizer ?? new DurableJobContextAuthorizer(
            contextFactory,
            new ProviderPolicyOptions());
    }

    public async Task<DurableJobEnqueueResult> EnqueueAsync<T>(
        DurableJobEnqueueRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("durable-job.enqueue");
        ValidateEnqueueRequest(
            request.Type,
            request.IdempotencyKey,
            request.Priority,
            request.MaxAttempts,
            request.MaxDeferrals);
        var payloadJson = _payloadPolicy.SerializeAndValidate(request.Payload);
        var savedContext = await _contextAuthorizer.AuthorizeEnqueueAsync(
            request.TenantId,
            request.OwnerUserId,
            request.ProviderAccountId,
            request.LibraryScopeId,
            request.Capability,
            request.CorrelationId,
            cancellationToken);
        var scopeKey = CreateUserScopeKey(
            savedContext.TenantId,
            savedContext.OwnerUserId);
        var type = request.Type.Trim().ToLowerInvariant();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var now = _clock.UtcNow;
        var maxAttempts = request.MaxAttempts ?? _options.DefaultMaxAttempts;
        var maxDeferrals = request.MaxDeferrals ?? _options.DefaultMaxDeferrals;
        var requestFingerprint = CreateRequestFingerprint(
            payloadJson,
            request.Priority,
            maxAttempts,
            maxDeferrals,
            request.AvailableAt);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Jobs.AsNoTracking().SingleOrDefaultAsync(
            item => item.ScopeKey == scopeKey &&
                    item.Type == type &&
                    item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing != null)
        {
            EnsureIdempotentRequestMatches(existing, savedContext, requestFingerprint);
            return new DurableJobEnqueueResult(existing.Id, false);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var job = new DurableJobRecord
        {
            Id = Guid.CreateVersion7(),
            ScopeKey = scopeKey,
            TenantId = savedContext.TenantId,
            OwnerUserId = savedContext.OwnerUserId,
            ProviderAccountId = savedContext.ProviderAccountId,
            LibraryScopeId = savedContext.LibraryScopeId,
            ProviderCapability = savedContext.ProviderCapability,
            PolicySnapshotJson = savedContext.PolicySnapshotJson,
            RequestFingerprint = requestFingerprint,
            CorrelationId = savedContext.CorrelationId,
            Type = type,
            PayloadJson = payloadJson,
            IdempotencyKey = idempotencyKey,
            State = DurableJobState.Pending,
            Priority = request.Priority,
            MaxAttempts = maxAttempts,
            MaxDeferrals = maxDeferrals,
            AvailableAt = request.AvailableAt ?? now,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Jobs.Add(job);
        context.OutboxMessages.Add(CreateOutbox(
            savedContext.TenantId,
            "job.enqueued",
            new { jobId = job.Id, jobType = job.Type },
            now));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            PlatformDiagnostics.JobsEnqueued.Add(
                1,
                new KeyValuePair<string, object?>("job.type", job.Type));
            activity?.SetTag("job.id", job.Id);
            activity?.SetTag("job.type", job.Type);
            return new DurableJobEnqueueResult(job.Id, true);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await using var retryContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var concurrent = await retryContext.Jobs.AsNoTracking().SingleAsync(
                item => item.ScopeKey == scopeKey &&
                        item.Type == type &&
                        item.IdempotencyKey == idempotencyKey,
                cancellationToken);
            EnsureIdempotentRequestMatches(concurrent, savedContext, requestFingerprint);
            return new DurableJobEnqueueResult(concurrent.Id, false);
        }
    }

    internal async Task<DurableJobEnqueueResult> EnqueueInExistingTransactionAsync<T>(
        AllstarrDbContext context,
        DurableJobEnqueueRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        ValidateEnqueueRequest(request.Type, request.IdempotencyKey, request.Priority, request.MaxAttempts, request.MaxDeferrals);
        var payloadJson = _payloadPolicy.SerializeAndValidate(request.Payload);
        var savedContext = await _contextAuthorizer.AuthorizeEnqueueAsync(
            request.TenantId, request.OwnerUserId, request.ProviderAccountId, request.LibraryScopeId,
            request.Capability, request.CorrelationId, cancellationToken);
        var scopeKey = CreateUserScopeKey(savedContext.TenantId, savedContext.OwnerUserId);
        var type = request.Type.Trim().ToLowerInvariant();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var now = _clock.UtcNow;
        var maxAttempts = request.MaxAttempts ?? _options.DefaultMaxAttempts;
        var maxDeferrals = request.MaxDeferrals ?? _options.DefaultMaxDeferrals;
        var requestFingerprint = CreateRequestFingerprint(
            payloadJson, request.Priority, maxAttempts, maxDeferrals, request.AvailableAt);
        var existing = await context.Jobs.SingleOrDefaultAsync(
            item => item.ScopeKey == scopeKey && item.Type == type && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing != null)
        {
            EnsureIdempotentRequestMatches(existing, savedContext, requestFingerprint);
            return new DurableJobEnqueueResult(existing.Id, false);
        }

        var job = new DurableJobRecord
        {
            Id = Guid.CreateVersion7(),
            ScopeKey = scopeKey,
            TenantId = savedContext.TenantId,
            OwnerUserId = savedContext.OwnerUserId,
            ProviderAccountId = savedContext.ProviderAccountId,
            LibraryScopeId = savedContext.LibraryScopeId,
            ProviderCapability = savedContext.ProviderCapability,
            PolicySnapshotJson = savedContext.PolicySnapshotJson,
            RequestFingerprint = requestFingerprint,
            CorrelationId = savedContext.CorrelationId,
            Type = type,
            PayloadJson = payloadJson,
            IdempotencyKey = idempotencyKey,
            State = DurableJobState.Pending,
            Priority = request.Priority,
            MaxAttempts = maxAttempts,
            MaxDeferrals = maxDeferrals,
            AvailableAt = request.AvailableAt ?? now,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Jobs.Add(job);
        context.OutboxMessages.Add(CreateOutbox(savedContext.TenantId, "job.enqueued", new { jobId = job.Id, jobType = job.Type }, now));
        return new DurableJobEnqueueResult(job.Id, true);
    }

    public async Task<DurableJobClaim?> ClaimNextAsync(
        string workerId,
        IReadOnlyCollection<string>? supportedTypes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentException("Worker ID is required and must be at most 200 characters.", nameof(workerId));
        }

        var normalizedTypes = supportedTypes?
            .Select(type => type.Trim().ToLowerInvariant())
            .ToArray();
        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                return await ClaimOnceAsync(workerId.Trim(), normalizedTypes, cancellationToken);
            }
            catch (Exception exception) when (retry < 2 && PostgresConcurrency.IsRetryable(exception))
            {
            }
        }

        return null;
    }

    private async Task<DurableJobClaim?> ClaimOnceAsync(
        string workerId,
        string[]? supportedTypes,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var query = context.Jobs.Where(item =>
            ((item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled) &&
             item.AvailableAt <= now) ||
            (item.State == DurableJobState.Running && item.LeaseExpiresAt <= now));
        if (supportedTypes is { Length: > 0 })
        {
            query = query.Where(item => supportedTypes.Contains(item.Type));
        }

        var job = await query
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.AvailableAt)
            .ThenBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job == null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (job.CancellationRequestedAt.HasValue)
        {
            job.State = DurableJobState.Cancelled;
            job.CompletedAt = now;
            job.UpdatedAt = now;
            job.Revision++;
            context.OutboxMessages.Add(CreateOutbox(
                job.TenantId,
                "job.cancelled",
                new { jobId = job.Id, jobType = job.Type },
                now));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (job.State == DurableJobState.Running)
        {
            var abandoned = await context.JobAttempts.SingleOrDefaultAsync(
                item => item.JobId == job.Id && item.CompletedAt == null,
                cancellationToken);
            if (abandoned != null)
            {
                abandoned.CompletedAt = now;
                abandoned.Outcome = "lease_expired";
                abandoned.ErrorCode = "worker_lease_expired";
                abandoned.ErrorMessage = "The prior worker lease expired before completion.";
            }

            job.FailureCount++;
            if (job.FailureCount >= job.MaxAttempts)
            {
                job.State = DurableJobState.Failed;
                job.CompletedAt = now;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                job.LastErrorCode = "worker_lease_expired";
                job.LastErrorMessage = "The job exhausted its recovery budget after worker lease loss.";
                job.UpdatedAt = now;
                job.Revision++;
                context.OutboxMessages.Add(CreateOutbox(
                    job.TenantId,
                    "job.failed",
                    new { jobId = job.Id, jobType = job.Type, errorCode = job.LastErrorCode },
                    now));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        job.AttemptCount++;
        job.State = DurableJobState.Running;
        job.LeaseOwner = workerId;
        job.LeaseExpiresAt = now.AddSeconds(_options.LeaseSeconds);
        job.StartedAt ??= now;
        job.UpdatedAt = now;
        job.Revision++;
        var attempt = new JobAttemptRecord
        {
            Id = Guid.CreateVersion7(),
            JobId = job.Id,
            AttemptNumber = job.AttemptCount,
            WorkerId = workerId,
            StartedAt = now
        };
        context.JobAttempts.Add(attempt);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PlatformDiagnostics.JobsClaimed.Add(
            1,
            new KeyValuePair<string, object?>("job.type", job.Type));
        using var payload = JsonDocument.Parse(job.PayloadJson);
        using var policySnapshot = JsonDocument.Parse(job.PolicySnapshotJson);
        return new DurableJobClaim(
            job.Id,
            attempt.Id,
            attempt.AttemptNumber,
            job.Type,
            payload.RootElement.Clone(),
            job.TenantId,
            job.OwnerUserId,
            job.ProviderAccountId,
            job.LibraryScopeId,
            job.ProviderCapability,
            policySnapshot.RootElement.Clone(),
            job.CorrelationId,
            workerId,
            job.LeaseExpiresAt.Value);
    }

    public async Task<bool> RenewLeaseAsync(
        DurableJobClaim claim,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.Jobs.SingleOrDefaultAsync(item => item.Id == claim.JobId, cancellationToken);
        if (job == null ||
            job.State != DurableJobState.Running ||
            job.LeaseOwner != claim.WorkerId ||
            job.AttemptCount != claim.AttemptNumber ||
            job.LeaseExpiresAt <= now)
        {
            return false;
        }

        if (job.CancellationRequestedAt.HasValue)
        {
            return false;
        }

        job.LeaseExpiresAt = now.AddSeconds(_options.LeaseSeconds);
        job.UpdatedAt = now;
        job.Revision++;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<DurableJobContextAuthorization> ReauthorizeAsync(
        DurableJobClaim claim,
        CancellationToken cancellationToken = default) =>
        _contextAuthorizer.ReauthorizeAsync(claim, cancellationToken);

    public async Task<bool> ReportProgressAsync(
        DurableJobClaim claim,
        DurableJobProgressUpdate update,
        CancellationToken cancellationToken = default)
    {
        var stage = SafeOperationalText.Sanitize(update.Stage, 100) ?? "progress";
        var message = SafeOperationalText.Sanitize(update.Message, 500) ?? "Work is continuing.";
        var provider = SafeOperationalText.Sanitize(update.Provider, 100);
        var playlist = SafeOperationalText.Sanitize(update.Playlist, 300);
        var track = SafeOperationalText.Sanitize(update.Track, 500);
        var deferralReason = SafeOperationalText.Sanitize(update.DeferralReason, 500);
        var throughputPerSecond = update.ThroughputPerSecond is >= 0 and < 10000
            ? update.ThroughputPerSecond
            : null;
        int? total = update.Total.HasValue ? Math.Max(0, update.Total.Value) : null;
        int? completed = update.Completed.HasValue ? Math.Max(0, update.Completed.Value) : null;
        if (total is > 0 && completed > total)
        {
            completed = total;
        }

        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.Jobs.SingleOrDefaultAsync(
            item => item.Id == claim.JobId &&
                    item.State == DurableJobState.Running &&
                    item.LeaseOwner == claim.WorkerId &&
                    item.AttemptCount == claim.AttemptNumber,
            cancellationToken);
        if (job == null)
        {
            return false;
        }

        context.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = job.TenantId,
            ActorUserId = job.OwnerUserId,
            Category = "job-progress",
            Action = stage,
            Outcome = "running",
            CorrelationId = job.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                attempt = job.AttemptCount,
                stage,
                message,
                completed,
                total,
                provider,
                playlist,
                track,
                deferralReason,
                throughputPerSecond
            }),
            CreatedAt = now
        });
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task CompleteAsync(
        DurableJobClaim claim,
        DurableJobCompletion completion,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var job = await context.Jobs.SingleAsync(item => item.Id == claim.JobId, cancellationToken);
        if (job.State != DurableJobState.Running ||
            job.LeaseOwner != claim.WorkerId ||
            job.AttemptCount != claim.AttemptNumber)
        {
            throw new InvalidOperationException("The worker no longer owns this job lease.");
        }

        var attempt = await context.JobAttempts.SingleAsync(
            item => item.Id == claim.AttemptId,
            cancellationToken);
        var effectiveKind = job.CancellationRequestedAt.HasValue
            ? DurableJobCompletionKind.Cancelled
            : completion.Kind;
        var errorCode = SafeOperationalText.Sanitize(completion.ErrorCode, 100);
        var safeMessage = SafeOperationalText.Sanitize(completion.SafeMessage);
        switch (effectiveKind)
        {
            case DurableJobCompletionKind.Succeeded:
                job.State = DurableJobState.Succeeded;
                job.CompletedAt = now;
                attempt.Outcome = "succeeded";
                break;
            case DurableJobCompletionKind.Cancelled:
                job.State = DurableJobState.Cancelled;
                job.CompletedAt = now;
                attempt.Outcome = "cancelled";
                break;
            case DurableJobCompletionKind.Retry:
                job.FailureCount++;
                if (job.FailureCount < job.MaxAttempts)
                {
                    job.State = DurableJobState.RetryScheduled;
                    job.AvailableAt = now.Add(completion.RetryDelay ?? RetryDelay(job.FailureCount));
                    attempt.Outcome = "retry_scheduled";
                }
                else
                {
                    job.State = DurableJobState.Failed;
                    job.CompletedAt = now;
                    attempt.Outcome = "failed";
                }
                break;
            case DurableJobCompletionKind.Deferred:
                job.DeferralCount++;
                if (job.DeferralCount <= job.MaxDeferrals)
                {
                    job.State = DurableJobState.RetryScheduled;
                    job.AvailableAt = now.Add(completion.RetryDelay ?? TimeSpan.FromMinutes(5));
                    attempt.Outcome = "deferred";
                }
                else
                {
                    job.State = DurableJobState.Failed;
                    job.CompletedAt = now;
                    attempt.Outcome = "failed";
                    errorCode = "deferral_limit_exceeded";
                    safeMessage = "The job remained blocked beyond its configured deferral budget.";
                }
                break;
            default:
                if (effectiveKind == DurableJobCompletionKind.Failed)
                {
                    job.FailureCount++;
                }
                job.State = DurableJobState.Failed;
                job.CompletedAt = now;
                attempt.Outcome = "failed";
                break;
        }

        attempt.CompletedAt = now;
        attempt.ErrorCode = errorCode;
        attempt.ErrorMessage = safeMessage;
        job.LastErrorCode = errorCode;
        job.LastErrorMessage = safeMessage;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;
        job.Revision++;
        var eventType = job.State switch
        {
            DurableJobState.Succeeded => "job.succeeded",
            DurableJobState.Failed => "job.failed",
            DurableJobState.Cancelled => "job.cancelled",
            DurableJobState.RetryScheduled when effectiveKind == DurableJobCompletionKind.Deferred => "job.deferred",
            _ => "job.retry-scheduled"
        };
        context.OutboxMessages.Add(CreateOutbox(
            job.TenantId,
            eventType,
            new
            {
                jobId = job.Id,
                jobType = job.Type,
                attempt = job.AttemptCount,
                errorCode = job.LastErrorCode
            },
            now));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PlatformDiagnostics.JobsCompleted.Add(
            1,
            new("job.type", job.Type),
            new("job.state", job.State.ToString().ToLowerInvariant()));
    }

    public async Task<bool> RequestCancellationAsync(
        Guid jobId,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var job = await context.Jobs.SingleOrDefaultAsync(
            item => item.Id == jobId && item.TenantId == tenantId,
            cancellationToken);
        if (job == null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (job.State == DurableJobState.Cancelled && job.CancellationRequestedAt.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        if (job.State is DurableJobState.Succeeded or DurableJobState.Failed or DurableJobState.Cancelled)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (job.CancellationRequestedAt.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        job.CancellationRequestedAt = now;
        if (job.State is DurableJobState.Pending or DurableJobState.RetryScheduled)
        {
            job.State = DurableJobState.Cancelled;
            job.CompletedAt = now;
            context.OutboxMessages.Add(CreateOutbox(
                job.TenantId,
                "job.cancelled",
                new { jobId = job.Id, jobType = job.Type },
                now));
        }

        job.UpdatedAt = now;
        job.Revision++;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static void ValidateEnqueueRequest(
        string type,
        string idempotencyKey,
        int priority,
        int? maxAttempts,
        int? maxDeferrals)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 200)
        {
            throw new ArgumentException("Job type is required and must be at most 200 characters.", nameof(type));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 300)
        {
            throw new ArgumentException(
                "Job idempotency key is required and must be at most 300 characters.",
                nameof(idempotencyKey));
        }

        if (priority is < -1000 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        if (maxAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }


        if (maxDeferrals is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDeferrals));
        }
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempt, 8))));

    private static void EnsureIdempotentRequestMatches(
        DurableJobRecord existing,
        DurableJobSavedContext requested,
        string requestFingerprint)
    {
        // Correlation IDs identify individual requests and are intentionally not part of the semantic
        // idempotency context. All authorization, account, library, and policy fields must match exactly.
        if (existing.TenantId != requested.TenantId ||
            existing.OwnerUserId != requested.OwnerUserId ||
            existing.ProviderAccountId != requested.ProviderAccountId ||
            !string.Equals(existing.LibraryScopeId, requested.LibraryScopeId, StringComparison.Ordinal) ||
            !string.Equals(existing.ProviderCapability, requested.ProviderCapability, StringComparison.Ordinal) ||
            !string.Equals(existing.PolicySnapshotJson, requested.PolicySnapshotJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The idempotency key is already bound to a different durable job execution context.");
        }

        if (!string.Equals(
                existing.RequestFingerprint,
                requestFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The idempotency key is already bound to a different durable job request payload or execution policy.");
        }
    }

    private static string CreateRequestFingerprint(
        string canonicalPayloadJson,
        int priority,
        int maxAttempts,
        int maxDeferrals,
        DateTimeOffset? availableAt)
    {
        using var payload = JsonDocument.Parse(canonicalPayloadJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("payload");
            payload.RootElement.WriteTo(writer);
            writer.WriteNumber("priority", priority);
            writer.WriteNumber("maxAttempts", maxAttempts);
            writer.WriteNumber("maxDeferrals", maxDeferrals);
            if (availableAt.HasValue)
            {
                writer.WriteString("availableAt", availableAt.Value.ToUniversalTime());
            }
            else
            {
                writer.WriteNull("availableAt");
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string CreateUserScopeKey(Guid tenantId, Guid ownerUserId) =>
        $"{tenantId:N}:{ownerUserId:N}";

    internal OutboxMessageRecord CreateOutbox(
        Guid? tenantId,
        string type,
        object payload,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload),
            State = OutboxMessageState.Pending,
            AvailableAt = now,
            MaxAttempts = _options.MaxOutboxAttempts,
            CreatedAt = now,
            UpdatedAt = now
        };
}

internal static class PostgresConcurrency
{
    public static bool IsRetryable(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException ||
                current is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }
}
