using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed class ListeningProfileService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock)
{
    public async Task<ListeningProfile> BuildAsync(IntelligenceScope scope, CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope); await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (policy?.Enabled != true) throw new InvalidOperationException("Intelligence is not enabled for this scope.");
        await IntelligencePolicyService.ScopedSignals(db, scope).Where(x => x.ExpiresAt <= clock.UtcNow).ExecuteDeleteAsync(cancellationToken);
        if (IntelligencePolicyService.RetentionCutoff(clock.UtcNow, policy.RetentionDays) is { } profileCutoff)
            await IntelligencePolicyService.ScopedProfiles(db, scope).Where(x => x.CreatedAt < profileCutoff).ExecuteDeleteAsync(cancellationToken);
        var signals = await IntelligencePolicyService.ScopedSignals(db, scope).AsNoTracking().Where(x => x.ExpiresAt > clock.UtcNow).ToListAsync(cancellationToken);
        var start = signals.Count == 0 ? clock.UtcNow : signals.Min(x => x.ObservedAt);
        var weighted = signals.GroupBy(x => x.TrackReference).Select(group => new
        {
            Track = group.Key,
            Weight = group.Sum(signal => SignalWeight(signal.SignalType) * signal.Value *
                Math.Pow(.5, Math.Max(0, (clock.UtcNow - signal.ObservedAt).TotalDays) / 30d))
        }).Where(x => x.Weight > 0).OrderByDescending(x => x.Weight).ThenBy(x => x.Track, StringComparer.Ordinal).Take(100).ToArray();
        var profile = new ListeningProfile(scope.TenantId, scope.OwnerUserId, scope.BackendInstanceId,
            scope.LibraryScopeId, signals.Count(x => x.SignalType is "play" or "complete"), signals.Count(x => x.SignalType == "skip"),
            signals.Count(x => x.SignalType == "favorite"), new Dictionary<string, double>(), start, clock.UtcNow)
        { TopTrackKeys = weighted.Select(x => x.Track).ToArray() };
        db.ListeningProfiles.Add(new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            Protocol = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            LibraryScopeId = scope.LibraryScopeId,
            ProfileJson = JsonSerializer.Serialize(profile),
            WindowStart = start,
            WindowEnd = clock.UtcNow,
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken); return profile;
    }
    private static double SignalWeight(string type) => type switch
    {
        "favorite" => 2,
        "complete" => 1.5,
        "playlist" => 1.2,
        "play" => 1,
        "skip" => -1.5,
        _ => 0
    };
}

public sealed class SmartPlaylistService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    DurableJobQueue jobs) : ISmartPlaylistService
{
    public async Task<Guid> CreateGeneratedSetAsync(IntelligenceScope scope, Guid runId, string name,
        IReadOnlyList<RecommendationCandidate> candidates, CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope); name = ValidName(name);
        candidates = candidates.Where(item => item.Exclusions.Count == 0).ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await IntelligencePolicyService.OwnsBackendAsync(db, scope, cancellationToken))
            throw new UnauthorizedAccessException("The generated playlist backend identity is outside this scope.");
        var existing = await db.GeneratedSets.AsNoTracking().SingleOrDefaultAsync(x => x.RunId == runId && x.TenantId == scope.TenantId && x.OwnerUserId == scope.OwnerUserId, cancellationToken);
        if (existing != null) { await EnqueueMaterialization(existing, cancellationToken); return existing.Id; }
        if (!await IntelligencePolicyService.ScopedRuns(db, scope).AnyAsync(x => x.Id == runId && x.State == RecommendationRunState.Succeeded, cancellationToken)) throw new UnauthorizedAccessException("The recommendation run is outside this scope or incomplete.");
        var completedRun = await IntelligencePolicyService.ScopedRuns(db, scope).AsNoTracking().SingleAsync(x => x.Id == runId, cancellationToken);
        var set = new GeneratedSetRecord
        {
            Id = Guid.CreateVersion7(),
            RunId = runId,
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            Protocol = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            LibraryScopeId = scope.LibraryScopeId,
            Name = name,
            TargetCredentialReferenceId = completedRun.TargetCredentialReferenceId,
            ScheduleId = completedRun.ScheduleId,
            MaterializationState = GeneratedSetMaterializationState.Pending,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
            Revision = 1
        }; db.GeneratedSets.Add(set); AddEntries(db, set, candidates);
        await db.SaveChangesAsync(cancellationToken); await EnqueueMaterialization(set, cancellationToken); return set.Id;
    }
    public async Task<Guid> CreateGeneratedSetAsync(IntelligenceScope scope, string name,
        IReadOnlyList<RecommendationCandidate> candidates, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope); name = ValidName(name);
        if (candidates.Count is < 1 or > 200 ||
            candidates.Select(item => item.TrackKey).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new ArgumentException("Generated playlist songs are invalid.");
        idempotencyKey = idempotencyKey?.Trim() ?? "";
        if (idempotencyKey.Length is < 1 or > 300 || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("Generated playlist request key is invalid.");
        var setId = StableSetId(scope, idempotencyKey);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await IntelligencePolicyService.OwnsBackendAsync(db, scope, cancellationToken))
            throw new UnauthorizedAccessException("The generated playlist backend identity is outside this scope.");
        var existing = await db.GeneratedSets.AsNoTracking().SingleOrDefaultAsync(item => item.Id == setId,
            cancellationToken);
        if (existing != null)
        {
            var existingKeys = await db.GeneratedSetEntries.AsNoTracking()
                .Where(item => item.GeneratedSetId == setId).OrderBy(item => item.Position)
                .Select(item => item.TrackKey).ToArrayAsync(cancellationToken);
            if (existing.TenantId != scope.TenantId || existing.OwnerUserId != scope.OwnerUserId ||
                existing.Protocol != scope.Protocol || existing.BackendInstanceId != scope.BackendInstanceId ||
                existing.LibraryScopeId != scope.LibraryScopeId || existing.Name != name ||
                !existingKeys.SequenceEqual(candidates.Select(item => item.TrackKey), StringComparer.Ordinal))
                throw new ArgumentException("Generated playlist request key was already used.");
            await EnqueueMaterialization(existing, cancellationToken); return existing.Id;
        }
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (policy?.Enabled != true) throw new InvalidOperationException("Intelligence is not enabled for this exact scope.");
        var set = new GeneratedSetRecord
        {
            Id = setId,
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            Protocol = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            LibraryScopeId = scope.LibraryScopeId,
            Name = name,
            TargetCredentialReferenceId = policy.TargetCredentialReferenceId,
            MaterializationState = GeneratedSetMaterializationState.Pending,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
            Revision = 1
        };
        db.GeneratedSets.Add(set); AddEntries(db, set, candidates);
        await db.SaveChangesAsync(cancellationToken); await EnqueueMaterialization(set, cancellationToken); return set.Id;
    }
    private static string ValidName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
            throw new ArgumentException("Generated set name is invalid.");
        return name;
    }
    private static Guid StableSetId(IntelligenceScope scope, string idempotencyKey) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{scope.TenantId:N}\u001f{scope.OwnerUserId:N}\u001f{scope.Protocol}\u001f{scope.BackendInstanceId}\u001f{scope.LibraryScopeId}\u001f{idempotencyKey}"))[..16]);
    private static void AddEntries(AllstarrDbContext db, GeneratedSetRecord set,
        IReadOnlyList<RecommendationCandidate> candidates)
    {
        for (var i = 0; i < candidates.Count; i++) db.GeneratedSetEntries.Add(new()
        {
            Id = Guid.CreateVersion7(),
            GeneratedSetId = set.Id,
            TenantId = set.TenantId,
            OwnerUserId = set.OwnerUserId,
            Position = i,
            TrackKey = candidates[i].TrackKey,
            Score = candidates[i].Score,
            Source = candidates[i].Source,
            ExplanationJson = JsonSerializer.Serialize(candidates[i].Signals),
            IdentityJson = JsonSerializer.Serialize(candidates[i].Identity)
        });
    }
    private Task<DurableJobEnqueueResult> EnqueueMaterialization(GeneratedSetRecord set, CancellationToken token) =>
        jobs.EnqueueAsync(new DurableJobEnqueueRequest<GeneratedSetMaterializationPayload>(
            "smart-playlist.materialize", set.ScheduleId is { } scheduleId
                ? $"schedule:{scheduleId:N}:materialize:{set.Id:N}"
                : $"generated-set:{set.Id:N}", new(set.Id), set.TenantId,
            set.OwnerUserId, LibraryScopeId: set.LibraryScopeId));
}
public sealed record GeneratedSetMaterializationPayload(Guid GeneratedSetId);

public sealed class RecommendationRunService(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    IPlatformClock clock) : IRecommendationRunService
{
    public async Task<RecommendationRunReceipt> EnqueueAsync(IntelligenceScope scope, IReadOnlyList<string> seeds,
        int limit, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope); if (limit is < 1 or > 500 || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 300 || seeds.Count > 100) throw new ArgumentException("The recommendation run request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await IntelligencePolicyService.OwnsBackendAsync(db, scope, cancellationToken))
            throw new UnauthorizedAccessException("The recommendation backend identity is outside this scope.");
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (policy?.Enabled != true) throw new InvalidOperationException("Intelligence is not enabled for this exact scope.");
        var enabledProviders = JsonSerializer.Deserialize<string[]>(policy.EnabledProvidersJson) ?? [];
        if (enabledProviders.Length == 0) throw new InvalidOperationException("No recommendation provider is enabled for this scope.");
        var runCutoff = IntelligencePolicyService.RetentionCutoff(clock.UtcNow, policy.RetentionDays);
        var expiredRuns = runCutoff == null ? [] : await IntelligencePolicyService.ScopedRuns(db, scope)
            .Where(x => x.CompletedAt != null && x.CompletedAt < runCutoff.Value).ToListAsync(cancellationToken);
        if (expiredRuns.Count > 0) { db.RecommendationRuns.RemoveRange(expiredRuns); await db.SaveChangesAsync(cancellationToken); }
        var existing = await IntelligencePolicyService.ScopedRuns(db, scope).AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing != null) return new(existing.Id, existing.JobId, false, existing.State);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var runId = Guid.CreateVersion7(); var job = await jobs.EnqueueInExistingTransactionAsync(db,
            new DurableJobEnqueueRequest<RecommendationRunPayload>("recommendation.generate", idempotencyKey,
                new(runId), scope.TenantId, scope.OwnerUserId, LibraryScopeId: scope.LibraryScopeId), cancellationToken);
        db.RecommendationRuns.Add(new()
        {
            Id = runId,
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            Protocol = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            LibraryScopeId = scope.LibraryScopeId,
            JobId = job.JobId,
            IdempotencyKey = idempotencyKey,
            PolicySnapshotJson = JsonSerializer.Serialize(new RecommendationPolicySnapshot(policy.Revision, enabledProviders, policy.RetentionDays, policy.TargetCredentialReferenceId)),
            SeedTrackKeysJson = JsonSerializer.Serialize(seeds),
            Limit = limit,
            TargetCredentialReferenceId = policy.TargetCredentialReferenceId,
            State = RecommendationRunState.Pending,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        }); await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return new(runId, job.JobId, true, RecommendationRunState.Pending);
    }
}
public sealed record RecommendationRunPayload(Guid RunId);
public sealed record RecommendationPolicySnapshot(long Revision, IReadOnlyList<string> EnabledProviders, int RetentionDays,
    Guid? TargetCredentialReferenceId = null, RecommendationAutomationSnapshot? Automation = null);
public sealed record RecommendationAutomationSnapshot(Guid ScheduleId, DateTimeOffset ScheduledFor,
    string GeneratedSetName);

public sealed class RecommendationRunJobHandler(IDbContextFactory<AllstarrDbContext> factory,
    IEnumerable<IRecommendationProvider> providers, ListeningProfileService profiles, IPlatformClock clock,
    ISmartPlaylistService? smartPlaylists = null) : IDurableJobHandler
{
    public string JobType => "recommendation.generate";
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        var payload = execution.Claim.Payload.Deserialize<RecommendationRunPayload>(); if (payload == null) return DurableJobCompletion.Failure("recommendation_payload_invalid", "The recommendation request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken); var run = await db.RecommendationRuns.SingleOrDefaultAsync(x => x.Id == payload.RunId && x.JobId == execution.Claim.JobId && x.TenantId == execution.Claim.TenantId && x.OwnerUserId == execution.Claim.OwnerUserId, cancellationToken);
        if (run == null) return DurableJobCompletion.Failure("recommendation_run_missing", "The recommendation run is unavailable.");
        if (run.State == RecommendationRunState.Cancelled) return DurableJobCompletion.Cancelled();
        RecommendationPolicySnapshot snapshot; try { snapshot = JsonSerializer.Deserialize<RecommendationPolicySnapshot>(run.PolicySnapshotJson) ?? throw new JsonException(); } catch (JsonException) { return DurableJobCompletion.Failure("recommendation_policy_snapshot_invalid", "The recommendation policy snapshot is invalid."); }
        if (run.State == RecommendationRunState.Succeeded) return await EnsureScheduledSetAsync(db, run, snapshot.Automation, cancellationToken);
        var scope = new IntelligenceScope(run.TenantId, run.OwnerUserId, run.Protocol, run.BackendInstanceId, run.LibraryScopeId);
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (policy?.Enabled != true || !await IntelligencePolicyService.OwnsBackendAsync(db, scope, cancellationToken))
        { run.State = RecommendationRunState.Cancelled; run.CompletedAt = clock.UtcNow; await db.SaveChangesAsync(cancellationToken); return DurableJobCompletion.Cancelled(); }
        var enabled = snapshot.EnabledProviders.ToHashSet(StringComparer.Ordinal);
        var selectedProviders = providers.Where(item => enabled.Contains(item.Id))
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        run.State = RecommendationRunState.Running; run.CompletedAt = null; run.ErrorCode = null;
        run.UpdatedAt = clock.UtcNow; run.Revision++; await db.SaveChangesAsync(cancellationToken);
        await execution.ReportProgressAsync(new("recommendation.profile",
            "Building the retained listening profile.", 0, selectedProviders.Length), cancellationToken);
        var profile = await profiles.BuildAsync(scope, cancellationToken); var seeds = JsonSerializer.Deserialize<string[]>(run.SeedTrackKeysJson) ?? [];
        if (seeds.Length == 0) seeds = profile.TopTrackKeys.ToArray();
        var results = new List<RecommendationCandidate>();
        for (var providerIndex = 0; providerIndex < selectedProviders.Length; providerIndex++)
        {
            var provider = selectedProviders[providerIndex];
            await execution.ReportProgressAsync(new("recommendation.provider",
                $"Searching {provider.Id}.", providerIndex, selectedProviders.Length,
                Provider: provider.Id), cancellationToken);
            RecommendationProviderResult outcome; try { outcome = await provider.RecommendAsync(new(scope, run.Id, profile, seeds, run.Limit, run.IdempotencyKey, true, cancellationToken)); }
            catch (OperationCanceledException)
            {
                run.State = RecommendationRunState.Cancelled; run.ErrorCode = "recommendation_cancelled";
                run.CompletedAt = clock.UtcNow; run.UpdatedAt = clock.UtcNow; run.Revision++;
                await db.SaveChangesAsync(CancellationToken.None);
                return DurableJobCompletion.Cancelled();
            }
            catch
            {
                run.State = RecommendationRunState.Failed; run.ErrorCode = "recommendation_provider_temporary_failure";
                run.CompletedAt = clock.UtcNow; run.UpdatedAt = clock.UtcNow; run.Revision++;
                await db.SaveChangesAsync(CancellationToken.None);
                return DurableJobCompletion.Retry("recommendation_provider_temporary_failure", "A recommendation provider temporarily failed.");
            }
            if (outcome.State == RecommendationProviderState.Succeeded)
                results.AddRange(outcome.Candidates.Take(run.Limit * 4));
            await execution.ReportProgressAsync(new("recommendation.provider",
                $"Finished {provider.Id}.", providerIndex + 1, selectedProviders.Length,
                Provider: provider.Id), cancellationToken);
        }
        var excludedTrackKeys = await db.RecommendationFeedback.AsNoTracking().Where(item =>
                item.TenantId == run.TenantId && item.OwnerUserId == run.OwnerUserId &&
                item.Protocol == run.Protocol && item.BackendInstanceId == run.BackendInstanceId &&
                item.LibraryScopeId == run.LibraryScopeId &&
                (item.Kind == "dislike" || item.Kind == "dismiss"))
            .Select(item => item.TrackKey).Distinct().ToListAsync(cancellationToken);
        var excluded = excludedTrackKeys.ToHashSet(StringComparer.Ordinal);
        var deduplicated = results.Where(Valid).GroupBy(x => x.TrackKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score).ThenBy(x => x.TrackKey, StringComparer.Ordinal).ToArray();
        var ordered = deduplicated.Where(item => !excluded.Contains(item.TrackKey)).Take(run.Limit)
            .Concat(deduplicated.Where(item => excluded.Contains(item.TrackKey)).Take(Math.Min(100, run.Limit))
                .Select(item => item with { Exclusions = ["user-feedback"] })).ToArray();
        await execution.ReportProgressAsync(new("recommendation.rank",
            $"Ranking {ordered.Length} candidate tracks.", 0, ordered.Length), cancellationToken);
        ordered = await ResolveProvenanceAsync(db, run, ordered, cancellationToken);
        db.RecommendationCandidates.RemoveRange(db.RecommendationCandidates.Where(x => x.RunId == run.Id));
        for (var i = 0; i < ordered.Length; i++) db.RecommendationCandidates.Add(new() { Id = Guid.CreateVersion7(), RunId = run.Id, TenantId = run.TenantId, OwnerUserId = run.OwnerUserId, Position = i, TrackKey = ordered[i].TrackKey, Score = ordered[i].Score, Source = ordered[i].Source, SignalsJson = JsonSerializer.Serialize(ordered[i].Signals), IdentityJson = JsonSerializer.Serialize(ordered[i].Identity), CanonicalRecordingId = ordered[i].CanonicalRecordingId, ProviderAccountId = ordered[i].ProviderAccountId, SourceRevision = ordered[i].SourceRevision ?? $"run:{run.Id:N}", ExclusionsJson = JsonSerializer.Serialize(ordered[i].Exclusions), CreatedAt = clock.UtcNow, Revision = 1 });
        run.State = RecommendationRunState.Succeeded; run.CompletedAt = clock.UtcNow; run.UpdatedAt = clock.UtcNow; run.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await execution.ReportProgressAsync(new("recommendation.complete",
            $"Saved {ordered.Length} recommendation tracks.", ordered.Length, ordered.Length), cancellationToken);
        return await EnsureScheduledSetAsync(db, run, snapshot.Automation, cancellationToken);
    }
    private async Task<DurableJobCompletion> EnsureScheduledSetAsync(AllstarrDbContext db,
        RecommendationRunRecord run, RecommendationAutomationSnapshot? automation, CancellationToken cancellationToken)
    {
        if (automation == null) return DurableJobCompletion.Success();
        if (smartPlaylists == null || run.ScheduleId != automation.ScheduleId ||
            run.ScheduledFor != automation.ScheduledFor)
            return DurableJobCompletion.Failure("recommendation_schedule_invalid", "The scheduled generated playlist request is invalid.");
        RecommendationCandidate[] candidates;
        try
        {
            var records = await db.RecommendationCandidates.AsNoTracking().Where(item => item.RunId == run.Id &&
                    item.TenantId == run.TenantId && item.OwnerUserId == run.OwnerUserId).OrderBy(item => item.Position)
                .ToListAsync(cancellationToken);
            candidates = records.Where(item => item.ExclusionsJson == "[]").Select(item => new RecommendationCandidate(item.TrackKey, item.Score, item.Source,
                JsonSerializer.Deserialize<RecommendationSignal[]>(item.SignalsJson) ?? [],
                JsonSerializer.Deserialize<RecommendationTrackIdentity>(item.IdentityJson))
            {
                CanonicalRecordingId = item.CanonicalRecordingId,
                ProviderAccountId = item.ProviderAccountId,
                SourceRevision = item.SourceRevision
            }).ToArray();
        }
        catch (JsonException)
        {
            return DurableJobCompletion.Failure("recommendation_candidate_invalid", "The scheduled recommendation results are invalid.");
        }
        try
        {
            await smartPlaylists.CreateGeneratedSetAsync(new(run.TenantId, run.OwnerUserId, run.Protocol,
                run.BackendInstanceId, run.LibraryScopeId), run.Id, automation.GeneratedSetName, candidates,
                cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DurableJobCompletion.Cancelled();
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return DurableJobCompletion.Failure("recommendation_schedule_invalid", "The scheduled generated playlist request is invalid.");
        }
        catch
        {
            return DurableJobCompletion.Retry("recommendation_materialization_enqueue_failed",
                "The generated playlist could not be queued yet.");
        }
    }
    private static bool Valid(RecommendationCandidate x) => !string.IsNullOrWhiteSpace(x.TrackKey) && x.TrackKey.Length <= 500 && double.IsFinite(x.Score) && x.Score is >= 0 and <= 1 && x.Signals.Count is > 0 and <= 32 &&
        x.Signals.All(s => !string.IsNullOrWhiteSpace(s.Code) && s.Code.Length <= 100 && !string.IsNullOrWhiteSpace(s.Explanation) && s.Explanation.Length <= 1000 && double.IsFinite(s.Weight)) &&
        Bounded(x.SourceRevision, 300) && x.CanonicalRecordingId != Guid.Empty && x.ProviderAccountId != Guid.Empty &&
        x.Exclusions.Count <= 16 && x.Exclusions.All(item => Bounded(item, 100)) && ValidIdentity(x.Identity);
    private static bool ValidIdentity(RecommendationTrackIdentity? x) => x == null ||
        Bounded(x.ProviderId, 100) && Bounded(x.ProviderTrackId, 500) && Bounded(x.Title, 500) &&
        Bounded(x.Artist, 500) && Bounded(x.Album, 500) && Bounded(x.Isrc, 20) &&
        (x.MusicBrainzRecordingId == null || Guid.TryParse(x.MusicBrainzRecordingId, out _));
    private static bool Bounded(string? value, int max) => value == null || value.Length is > 0 && value.Length <= max && !value.Any(char.IsControl);

    private static async Task<RecommendationCandidate[]> ResolveProvenanceAsync(
        AllstarrDbContext db, RecommendationRunRecord run, RecommendationCandidate[] candidates,
        CancellationToken cancellationToken)
    {
        var libraryIds = candidates.Select(item => item.Identity?.LibraryTrackId).OfType<Guid>().Distinct().ToArray();
        var library = await db.LibraryTracks.AsNoTracking().Where(item => libraryIds.Contains(item.Id) &&
                item.TenantId == run.TenantId && item.OwnerUserId == run.OwnerUserId &&
                item.Protocol == run.Protocol && item.BackendInstanceId == run.BackendInstanceId &&
                item.LibraryScopeId == run.LibraryScopeId)
            .Select(item => new { item.Id, item.CanonicalRecordingId }).ToDictionaryAsync(item => item.Id, cancellationToken);
        var providerIds = candidates.Select(item => item.Identity?.ProviderId).Where(item => item != null).Distinct().ToArray();
        var externalIds = candidates.Select(item => item.Identity?.ProviderTrackId ?? item.Identity?.BackendItemId)
            .Where(item => item != null).Distinct().ToArray();
        var identities = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                item.TenantId == run.TenantId && providerIds.Contains(item.ProviderId) &&
                externalIds.Contains(item.ExternalId))
            .Select(item => new { item.ProviderId, item.ExternalId, item.CanonicalRecordingId, item.ProviderAccountId })
            .ToListAsync(cancellationToken);
        var musicBrainzIds = candidates.Select(item => item.Identity?.MusicBrainzRecordingId)
            .Where(item => item != null).Distinct().ToArray();
        var isrcs = candidates.Select(item => item.Identity?.Isrc).Where(item => item != null).Distinct().ToArray();
        var canonicals = await db.CanonicalRecordings.AsNoTracking().Where(item => item.TenantId == run.TenantId &&
                (musicBrainzIds.Contains(item.MusicBrainzRecordingId) || isrcs.Contains(item.Isrc)))
            .Select(item => new { item.Id, item.MusicBrainzRecordingId, item.Isrc }).ToListAsync(cancellationToken);
        var validAccounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled &&
                item.TenantId == run.TenantId &&
                (item.Scope == ProviderAccountScope.User && item.OwnerUserId == run.OwnerUserId ||
                 item.Scope == ProviderAccountScope.Library && item.LibraryScopeId == run.LibraryScopeId))
            .Select(item => new { item.Id, item.ProviderId }).ToListAsync(cancellationToken);

        return candidates.Select(candidate =>
        {
            var providerId = candidate.Identity?.ProviderId;
            var externalId = candidate.Identity?.ProviderTrackId ?? candidate.Identity?.BackendItemId;
            var providerMatches = identities.Where(item => item.ProviderId == providerId && item.ExternalId == externalId &&
                    (item.ProviderAccountId == null || validAccounts.Any(account =>
                        account.Id == item.ProviderAccountId && account.ProviderId == providerId)))
                .ToArray();
            var providerCanonical = providerMatches.Select(item => item.CanonicalRecordingId).Distinct().ToArray();
            var providerAccounts = providerMatches.Select(item => item.ProviderAccountId).OfType<Guid>().Distinct().ToArray();
            Guid? providerAccount = candidate.ProviderAccountId is { } accountId &&
                                  validAccounts.Any(item => item.Id == accountId && item.ProviderId == providerId)
                ? accountId : providerAccounts.Length == 1 ? providerAccounts[0] : null;
            var exactCanonicals = canonicals.Where(item =>
                candidate.Identity?.MusicBrainzRecordingId is { } mbid && item.MusicBrainzRecordingId == mbid ||
                candidate.Identity?.Isrc is { } isrc && item.Isrc == isrc)
                .Select(item => item.Id).Distinct().ToArray();
            var canonical = candidate.CanonicalRecordingId ??
                (candidate.Identity?.LibraryTrackId is { } libraryId &&
                library.TryGetValue(libraryId, out var track) ? track.CanonicalRecordingId : null) ??
                (providerCanonical.Length == 1 ? providerCanonical[0] : (Guid?)null) ??
                (exactCanonicals.Length == 1 ? exactCanonicals[0] : (Guid?)null);
            return candidate with { CanonicalRecordingId = canonical, ProviderAccountId = providerAccount };
        }).ToArray();
    }
}

public sealed class GeneratedSetMaterializationJobHandler(IDbContextFactory<AllstarrDbContext> factory,
    IEnumerable<IGeneratedSetMaterializer> materializers, IPlatformClock? clock = null) : IDurableJobHandler
{
    private DateTimeOffset Now => clock?.UtcNow ?? DateTimeOffset.UtcNow;
    public string JobType => "smart-playlist.materialize";
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        var payload = execution.Claim.Payload.Deserialize<GeneratedSetMaterializationPayload>();
        if (payload == null) return DurableJobCompletion.Failure("generated_set_payload_invalid", "The generated playlist request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var set = await db.GeneratedSets.SingleOrDefaultAsync(x => x.Id == payload.GeneratedSetId &&
            x.TenantId == execution.Claim.TenantId && x.OwnerUserId == execution.Claim.OwnerUserId &&
            x.LibraryScopeId == execution.Claim.LibraryScopeId, cancellationToken);
        if (set == null) return DurableJobCompletion.Failure("generated_set_missing", "The generated playlist is unavailable.");
        var target = materializers.SingleOrDefault(x => x.Protocol.Equals(set.Protocol, StringComparison.Ordinal));
        if (target == null) { set.MaterializationState = GeneratedSetMaterializationState.Unsupported; set.LastErrorCode = "generated_set_target_unsupported"; set.UpdatedAt = Now; set.Revision++; await db.SaveChangesAsync(cancellationToken); return DurableJobCompletion.Failure("generated_set_target_unsupported", "Generated playlist materialization is unsupported for this backend."); }
        set.MaterializationState = GeneratedSetMaterializationState.Running; set.LastErrorCode = null; set.UpdatedAt = Now; set.Revision++; await db.SaveChangesAsync(cancellationToken);
        var entries = await db.GeneratedSetEntries.AsNoTracking().Where(x => x.GeneratedSetId == set.Id &&
            x.TenantId == set.TenantId && x.OwnerUserId == set.OwnerUserId).OrderBy(x => x.Position)
            .ToListAsync(cancellationToken);
        var candidates = entries.Select(x => new RecommendationCandidate(x.TrackKey, x.Score, x.Source,
            JsonSerializer.Deserialize<RecommendationSignal[]>(x.ExplanationJson) ?? [],
            JsonSerializer.Deserialize<RecommendationTrackIdentity>(x.IdentityJson))).ToArray();
        await execution.ReportProgressAsync(new("playlist.materialize",
            $"Creating {set.Name}.", 0, candidates.Length, Playlist: set.Name), cancellationToken);
        GeneratedSetMaterializationResult result;
        try
        {
            result = await target.MaterializeAsync(new(new(set.TenantId, set.OwnerUserId, set.Protocol,
                set.BackendInstanceId, set.LibraryScopeId), set.Id, candidates, $"generated-set:{set.Id:N}"), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            set.MaterializationState = GeneratedSetMaterializationState.Cancelled; set.LastErrorCode = "generated_set_cancelled";
            set.UpdatedAt = Now; set.Revision++; await db.SaveChangesAsync(CancellationToken.None);
            return DurableJobCompletion.Cancelled();
        }
        if (result.Succeeded) { set.MaterializationState = GeneratedSetMaterializationState.Succeeded; set.BackendPlaylistId = result.BackendPlaylistId; set.TargetRevision = result.TargetRevision; set.MaterializedAt = Now; set.UpdatedAt = Now; set.Revision++; await db.SaveChangesAsync(cancellationToken); await execution.ReportProgressAsync(new("playlist.materialize", $"Created {set.Name}.", candidates.Length, candidates.Length, Playlist: set.Name), cancellationToken); return DurableJobCompletion.Success(); }
        set.MaterializationState = GeneratedSetMaterializationState.Failed; set.LastErrorCode = result.SafeErrorCode ?? "generated_set_failed"; set.UpdatedAt = Now; set.Revision++; await db.SaveChangesAsync(cancellationToken);
        return result.Retryable ? DurableJobCompletion.Retry(result.SafeErrorCode ?? "generated_set_retry", "Generated playlist materialization will retry.")
            : DurableJobCompletion.Failure(result.SafeErrorCode ?? "generated_set_failed", "Generated playlist materialization failed.");
    }
}

public static class IntelligenceRegistration
{
    public static IServiceCollection AddIntelligenceCore(this IServiceCollection services)
    {
        services.AddSingleton<IIntelligencePolicyService, IntelligencePolicyService>(); services.AddSingleton<IRecommendationSignalWriter, RecommendationSignalWriter>();
        services.AddSingleton<ListeningProfileService>(); services.AddSingleton<ISmartPlaylistService, SmartPlaylistService>();
        services.AddSingleton<IRecommendationRunService, RecommendationRunService>(); services.AddSingleton<IDurableJobHandler, RecommendationRunJobHandler>();
        services.AddSingleton<IRecommendationProviderStatusService, RecommendationProviderStatusService>();
        services.AddSingleton<ListeningIntakeTokenService>();
        services.AddSingleton<IDurableJobHandler, GeneratedSetMaterializationJobHandler>(); return services;
    }
}

public sealed class RecommendationProviderStatusService(IEnumerable<IRecommendationProvider> providers) : IRecommendationProviderStatusService
{
    public async Task<IReadOnlyList<RecommendationProviderReadiness>> ListAsync(IntelligenceScope scope, CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope); var values = new List<RecommendationProviderReadiness>();
        foreach (var provider in providers.OrderBy(x => x.Id, StringComparer.Ordinal)) values.Add(provider is IRecommendationProviderReadiness ready
            ? await ready.GetReadinessAsync(scope, cancellationToken) : new(provider.Id, RecommendationProviderReadinessState.Unsupported, "readiness-not-implemented"));
        return values;
    }
}
