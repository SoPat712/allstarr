using System.Data;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed partial class DurableJobQueue
{
    internal async Task<DurableJobEnqueueResult> EnqueueLibraryIndexAsync(
        DurableJobEnqueueRequest<LibraryIndexJobPayload> request, CancellationToken cancellationToken)
    {
        ValidateEnqueueRequest(request.Type, request.IdempotencyKey, request.Priority, request.MaxAttempts, request.MaxDeferrals);
        _payloadPolicy.SerializeAndValidate(request.Payload);
        var saved = await _contextAuthorizer.AuthorizeEnqueueAsync(request.OwnerUserId, request.ProviderAccountId,
            request.Capability, request.CorrelationId, cancellationToken, request.Type);
        var scopeKey = CreateSavedScopeKey(saved);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                var active = await db.Jobs.AsNoTracking().Where(job => job.Type == "library.index" &&
                    job.ScopeKey == scopeKey && job.CancellationRequestedAt == null &&
                    (job.State == DurableJobState.Pending || job.State == DurableJobState.Running || job.State == DurableJobState.RetryScheduled))
                    .OrderBy(job => job.CreatedAt).ToArrayAsync(cancellationToken);
                var existing = active.FirstOrDefault(job => SameLibraryScope(job.PayloadJson, request.Payload));
                var result = existing != null ? new DurableJobEnqueueResult(existing.Id, false) :
                    await EnqueueInExistingTransactionAsync(db, request, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception error) when (attempt < 2 && DbErrors.IsTransientConflict(error)) { }
        }
    }

    internal async Task ReconcileLibraryIndexesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var active = await db.Jobs.AsNoTracking().Where(job => job.Type == "library.index" &&
            job.CancellationRequestedAt == null &&
            (job.State == DurableJobState.Pending || job.State == DurableJobState.Running || job.State == DurableJobState.RetryScheduled))
            .OrderByDescending(job => job.State == DurableJobState.Running).ThenBy(job => job.CreatedAt).ThenBy(job => job.Id)
            .ToArrayAsync(cancellationToken);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in active)
        {
            var payload = ReadLibraryIndexPayload(job.PayloadJson);
            if (payload != null && !scopes.Add(LibraryIndexDiagnostics.Scope(job.OwnerUserId, payload)))
                await RequestCancellationAsync(job.Id, job.OwnerUserId, cancellationToken);
        }
    }

    internal async Task CloseUnavailableLibraryIndexesAsync(Guid owner, LibraryIndexJobPayload payload,
        string code, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var active = await db.Jobs.Where(job => job.Type == "library.index" && job.OwnerUserId == owner &&
            (job.State == DurableJobState.Pending || job.State == DurableJobState.Running || job.State == DurableJobState.RetryScheduled))
            .ToArrayAsync(cancellationToken);
        foreach (var job in active)
        {
            var saved = ReadLibraryIndexPayload(job.PayloadJson);
            if (saved == null || saved.BackendInstanceId != payload.BackendInstanceId || saved.BackendPrincipalId != payload.BackendPrincipalId) continue;
            if (job.State == DurableJobState.Running)
                job.CancellationRequestedAt ??= _clock.UtcNow;
            else
            {
                job.State = DurableJobState.Failed;
                job.CompletedAt = _clock.UtcNow;
                job.LastErrorCode = code;
                job.LastErrorMessage = "Background library indexing requires available stored backend access and library permissions.";
                job.LastAttemptOutcome = "skipped";
            }
            job.UpdatedAt = _clock.UtcNow;
            job.Revision++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool SameLibraryScope(string json, LibraryIndexJobPayload payload)
    {
        var saved = ReadLibraryIndexPayload(json);
        return saved != null && saved.BackendInstanceId == payload.BackendInstanceId &&
            saved.BackendPrincipalId == payload.BackendPrincipalId && saved.BackendLibraryId == payload.BackendLibraryId;
    }

    private static LibraryIndexJobPayload? ReadLibraryIndexPayload(string json)
    {
        try { return JsonSerializer.Deserialize<LibraryIndexJobPayload>(json); }
        catch (JsonException) { return null; }
    }
}
