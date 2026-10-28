using System.Data;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Health;

public sealed partial class HealthSmokeService
{
    private static readonly string EmptyArtifacts = JsonSerializer.Serialize(new HealthSmokeArtifacts(), Json);

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        var payload = execution.Claim.Payload.Deserialize<HealthSmokeJobPayload>();
        if (payload == null || execution.Claim.OwnerUserId == null)
            return DurableJobCompletion.Failure("health_scope_invalid", "The health check context is unavailable.");
        var runId = execution.Claim.JobId;
        if (await ReadRunAsync(runId, token) != null)
            return await CleanupRunAsync(runId, execution.Claim.OwnerUserId.Value, token);
        HealthSmokeRunBinding binding;
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            var schedule = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.Id == payload.ScheduleId &&
                item.JobType == JobType && item.OwnerUserId == execution.Claim.OwnerUserId, token);
            var config = schedule == null ? null : Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson);
            if (config is { IsServerMode: false } && IsJellyfin)
                return DurableJobCompletion.Failure("legacy_disabled", "The previous Jellyfin listener Health mode is disabled.");
            if (config is { IsServerMode: true } && !IsJellyfin)
                return DurableJobCompletion.Failure("health_scope_changed", "The saved Health mode belongs to a different backend.");
            if (config?.IsServerMode == true)
            {
                var administrator = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == execution.Claim.OwnerUserId &&
                    item.Enabled && item.IsAdmin && item.BackendType == backend.EffectiveValue.ToLower() &&
                    item.BackendInstanceId == identity.BackendInstanceId, token);
                if (administrator == null || config.Revision != payload.ConfigurationRevision)
                    return DurableJobCompletion.Failure("health_scope_changed", "The Health administrator or configuration changed.");
                binding = new(schedule!.Id, administrator.Id, backend.EffectiveValue, administrator.BackendInstanceId, "", config, clock.UtcNow);
            }
            else
            {
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == execution.Claim.OwnerUserId &&
                    item.Enabled && item.IsSynthetic && !item.IsAdmin, token);
                if (config == null || user == null || config.Revision != payload.ConfigurationRevision ||
                    !user.BackendType.Equals(backend.EffectiveValue, StringComparison.OrdinalIgnoreCase) || user.BackendInstanceId != identity.BackendInstanceId)
                    return DurableJobCompletion.Failure("health_scope_changed", "The dedicated listener or health configuration changed.");
                binding = new(schedule!.Id, user.Id, backend.EffectiveValue, user.BackendInstanceId, user.BackendPrincipalId, config, clock.UtcNow);
            }
        }
        await SaveAsync(runId, binding.OwnerUserId, "started", "running", binding, token);
        IReadOnlyList<HealthSmokeStep> results;
        try
        {
            await using var scan = await factory.CreateDbContextAsync(token);
            var pending = await scan.HealthSmokeRuns.AsNoTracking().Where(item => item.Id != runId &&
                    item.ArtifactsJson != "{}" && item.ArtifactsJson != EmptyArtifacts)
                .OrderBy(item => item.CreatedAt).Take(20).Select(item => new { item.Id, item.OwnerUserId }).ToArrayAsync(token);
            foreach (var previous in pending)
            {
                var cleanup = await CleanupRunAsync(previous.Id, previous.OwnerUserId, token);
                if (cleanup.Kind != DurableJobCompletionKind.Succeeded)
                    throw new HealthSmokeConfigurationException("cleanup_pending", "A previous temporary health item still needs cleanup.");
            }
            results = binding.Configuration.IsServerMode
                ? _serverProbe == null
                    ? throw new HealthSmokeConfigurationException("service_probe_unavailable", "The server Health probe is unavailable.")
                    : await _serverProbe.RunAsync(binding.Configuration.SearchQuery, binding.Configuration.FirstByteBudgetSeconds, token)
                : await probe.RunAsync(await ContextAsync(runId, binding, token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            var names = binding.Configuration.IsServerMode ? HealthSmokeServerProbe.StepNames : HealthSmokeProtocolProbe.StepNames;
            results = names.Select(name => new HealthSmokeStep(name,
                name == (binding.Configuration.IsServerMode ? "backend" : "cleanup") ? "failed" : "skipped", 0,
                "check_unavailable")).ToArray();
        }
        await CompleteAsync(runId, binding, results, token);
        return IsSuccessful(binding.Configuration.IsServerMode ? "server-api-key" : "listener", results) ? DurableJobCompletion.Success() :
            DurableJobCompletion.Failure("health_step_failed", "A health check step failed. Open Health for its result.");
    }

    public async Task<DurableJobCompletion> CleanupRunAsync(Guid runId, Guid owner, CancellationToken token)
    {
        var saved = await ReadRunAsync(runId, token);
        if (saved == null || saved.Value.Binding.OwnerUserId != owner)
            return DurableJobCompletion.Failure("health_scope_invalid", "The cleanup context is unavailable.");
        try
        {
            if (saved.Value.Artifacts != new HealthSmokeArtifacts())
                await probe.CleanupAsync(await ContextAsync(runId, saved.Value.Binding, token), saved.Value.Artifacts, token);
            await using var db = await factory.CreateDbContextAsync(token);
            var row = await db.HealthSmokeRuns.AsNoTracking().SingleAsync(item => item.Id == runId, token);
            if (row.CompletedJson == null)
            {
                var names = saved.Value.Binding.Configuration.IsServerMode ? HealthSmokeServerProbe.StepNames : HealthSmokeProtocolProbe.StepNames;
                await CompleteAsync(runId, saved.Value.Binding, names.Select(name => new HealthSmokeStep(name,
                    name == "cleanup" ? "passed" : "skipped", 0, name == "cleanup" ? null : "run_interrupted")).ToArray(), token);
            }
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return DurableJobCompletion.Retry("health_cleanup_failed", "Temporary health-check data still needs cleanup.", TimeSpan.FromMinutes(1));
        }
    }

    public async Task RecoverAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var pending = await db.HealthSmokeRuns.AsNoTracking().Where(item => item.CompletedJson == null ||
                item.ArtifactsJson != "{}" && item.ArtifactsJson != EmptyArtifacts)
            .OrderBy(item => item.CreatedAt).Take(100).Select(item => new { item.Id, item.OwnerUserId }).ToArrayAsync(token);
        foreach (var run in pending)
        {
            try
            {
                await jobs.EnqueueAsync(new DurableJobEnqueueRequest<HealthSmokeCleanupPayload>(CleanupJobType,
                    $"health-cleanup:{run.Id:N}", new(run.Id), run.OwnerUserId, MaxAttempts: 3), token);
            }
            catch (UnauthorizedAccessException) { }
        }
        if (IsJellyfin)
        {
            var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.JobType == JobType, token);
            var config = schedule == null ? null : Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson);
            if (schedule != null && config is { IsServerMode: false })
            {
                schedule.Enabled = false;
                schedule.NextRunAt = null;
                schedule.UpdatedAt = clock.UtcNow;
                if (config.CredentialReferenceId != Guid.Empty && schedule.OwnerUserId.HasValue &&
                    !await HasOutstandingArtifactsAsync(db, token))
                {
                    var reference = await db.SecretReferences.SingleOrDefaultAsync(item => item.Id == config.CredentialReferenceId &&
                        item.UserId == schedule.OwnerUserId && item.Purpose == ListenerPurpose && item.RevokedAt == null, token);
                    if (reference != null) reference.RevokedAt = clock.UtcNow;
                }
                await db.SaveChangesAsync(token);
            }
        }
    }

    private async Task CompleteAsync(Guid runId, HealthSmokeRunBinding binding, IReadOnlyList<HealthSmokeStep> results, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var row = await db.HealthSmokeRuns.SingleAsync(item => item.Id == runId && item.OwnerUserId == binding.OwnerUserId, token);
        if (row.CompletedJson != null) return;
        var previousRow = await db.HealthSmokeRuns.AsNoTracking().Where(item => item.OwnerUserId == binding.OwnerUserId &&
                item.CompletedJson != null && item.Id != runId)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
        var previous = previousRow?.CompletedJson == null ? null : Parse<HealthSmokeCompleted>(previousRow.CompletedJson);
        var sameRevision = previous?.ConfigurationRevision == binding.Configuration.Revision;
        var decision = HealthSmokeAlertPolicy.Evaluate(sameRevision ? previous!.Steps : [], results,
            sameRevision ? previous!.OpenAlertSteps : []);
        var alertStatus = decision.Changes.Count == 0 ? "none" : binding.Configuration.WebhookReferenceId == null ? "not_configured" : "pending";
        var completed = new HealthSmokeCompleted(binding.Protocol, binding.StartedAt, clock.UtcNow, binding.Configuration.Revision,
            results.ToArray(), decision.OpenSteps.ToArray(), alertStatus)
        {
            Mode = binding.Configuration.IsServerMode ? "server-api-key" : "listener"
        };
        row.CompletedJson = JsonSerializer.Serialize(completed, Json);
        row.CompletedAt = clock.UtcNow;
        row.AlertStatus = alertStatus;
        db.AuditEvents.Add(Event(runId, binding.OwnerUserId, "completed",
            IsSuccessful(binding.Configuration.IsServerMode ? "server-api-key" : "listener", results) ? "passed" : "failed", "{}", clock.UtcNow));
        if (problemAlerts != null)
            await problemAlerts.ObserveHealthWithinAsync(db, binding, completed, decision, runId, token, sameRevision ? previous : null);
        if (problemAlerts == null && binding.Configuration.WebhookReferenceId != null && decision.Changes.Count > 0)
            await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<HealthSmokeAlertPayload>(AlertJobType,
                $"health-alert:{runId:N}", new(runId, decision.Changes.ToArray()), binding.OwnerUserId, MaxAttempts: 3), token);
        if (row.ArtifactsJson != "{}" && row.ArtifactsJson != EmptyArtifacts)
            await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<HealthSmokeCleanupPayload>(CleanupJobType,
                $"health-cleanup:{runId:N}", new(runId), binding.OwnerUserId, MaxAttempts: 3), token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        var cutoff = clock.UtcNow.AddDays(-30);
        await db.HealthSmokeRuns.Where(item => item.CompletedAt < cutoff &&
            (item.ArtifactsJson == "{}" || item.ArtifactsJson == EmptyArtifacts) && item.AlertStatus != "pending")
            .ExecuteDeleteAsync(token);
    }
}

public sealed class HealthSmokeJobHandler(HealthSmokeService service) : IDurableJobHandler, IDurableJobRecoveryHandler
{
    public string JobType => HealthSmokeService.JobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token) => service.ExecuteAsync(context, token);
    public Task RecoverAsync(CancellationToken token) => service.RecoverAsync(token);
}

public sealed class HealthSmokeCleanupJobHandler(HealthSmokeService service) : IDurableJobHandler
{
    public string JobType => HealthSmokeService.CleanupJobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token)
    {
        var payload = context.Claim.Payload.Deserialize<HealthSmokeCleanupPayload>();
        return payload == null || context.Claim.OwnerUserId == null
            ? Task.FromResult(DurableJobCompletion.Failure("health_scope_invalid", "The cleanup context is unavailable."))
            : service.CleanupRunAsync(payload.RunId, context.Claim.OwnerUserId.Value, token);
    }
}
