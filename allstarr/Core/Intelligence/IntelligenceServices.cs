using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed record IntelligencePolicyInput(bool Enabled, int RetentionDays,
    IReadOnlyList<string> AllowedSignalTypes, IReadOnlyList<string> EnabledProviders,
    Guid? TargetCredentialReferenceId = null);
public sealed record RecommendationRunReceipt(Guid RunId, Guid JobId, bool Created, RecommendationRunState State);

public interface IIntelligencePolicyService
{
    Task<IntelligencePolicyRecord?> GetAsync(IntelligenceScope scope, CancellationToken cancellationToken = default);
    Task<IntelligencePolicyRecord> SetAsync(IntelligenceScope scope, IntelligencePolicyInput input, CancellationToken cancellationToken = default);
    Task DisableAndPurgeAsync(IntelligenceScope scope, CancellationToken cancellationToken = default);
}
public interface IRecommendationRunService
{
    Task<RecommendationRunReceipt> EnqueueAsync(IntelligenceScope scope, IReadOnlyList<string> seeds,
        int limit, string idempotencyKey, CancellationToken cancellationToken = default);
}

public sealed class IntelligencePolicyService(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    ListeningHistoryImportArtifactStore? historyArtifacts = null)
    : IIntelligencePolicyService
{
    internal const string SubsonicCredentialPurpose = "playlist-backend:subsonic";
    private static readonly HashSet<string> Signals = new(["play", "skip", "complete", "favorite", "playlist"], StringComparer.Ordinal);
    public async Task<IntelligencePolicyRecord?> GetAsync(IntelligenceScope scope, CancellationToken cancellationToken = default)
    { ValidateScope(scope); await using var db = await factory.CreateDbContextAsync(cancellationToken); return await Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken); }
    public async Task<IntelligencePolicyRecord> SetAsync(IntelligenceScope scope, IntelligencePolicyInput input, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope); if (input.RetentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(input));
        var signals = input.AllowedSignalTypes.Select(Normalize).Distinct().Order().ToArray();
        if (signals.Any(value => !Signals.Contains(value))) throw new ArgumentException("The intelligence signal type is unsupported.", nameof(input));
        var providers = input.EnabledProviders.Select(Normalize).Distinct().Order().ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.BackendIdentities.AsNoTracking().AnyAsync(item => item.TenantId == scope.TenantId &&
            item.UserId == scope.OwnerUserId && item.BackendType == scope.Protocol &&
            item.BackendInstanceId == scope.BackendInstanceId, cancellationToken))
            throw new UnauthorizedAccessException("The intelligence policy backend identity is outside this scope.");
        if (scope.Protocol == "jellyfin" && input.TargetCredentialReferenceId.HasValue ||
            scope.Protocol == "subsonic" && input.Enabled && !input.TargetCredentialReferenceId.HasValue)
            throw new ArgumentException("Subsonic intelligence requires an exact-scope target credential reference; Jellyfin does not accept one.", nameof(input));
        if (input.TargetCredentialReferenceId.HasValue && !await db.SecretReferences.AsNoTracking().AnyAsync(item =>
                item.Id == input.TargetCredentialReferenceId && item.TenantId == scope.TenantId &&
                item.Purpose == SubsonicCredentialPurpose && item.RevokedAt == null,
                cancellationToken))
            throw new UnauthorizedAccessException("The intelligence target credential is outside this tenant or revoked.");
        var record = await Query(db, scope).SingleOrDefaultAsync(cancellationToken);
        var now = clock.UtcNow; record ??= new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            Protocol = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            LibraryScopeId = scope.LibraryScopeId,
            CreatedAt = now
        };
        if (db.Entry(record).State == EntityState.Detached) db.IntelligencePolicies.Add(record);
        record.Enabled = input.Enabled; record.RetentionDays = input.RetentionDays;
        record.TargetCredentialReferenceId = input.TargetCredentialReferenceId;
        record.AllowedSignalTypesJson = JsonSerializer.Serialize(signals); record.EnabledProvidersJson = JsonSerializer.Serialize(providers);
        record.UpdatedAt = now; record.Revision++;
        if (!input.Enabled)
            await DisableSchedulesAsync(db, scope, record.Id, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); return record;
    }
    public async Task DisableAndPurgeAsync(IntelligenceScope scope, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope); await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var policy = await Query(db, scope).SingleOrDefaultAsync(cancellationToken);
        if (policy != null) { policy.Enabled = false; policy.UpdatedAt = clock.UtcNow; policy.Revision++; }
        var imports = await db.ListeningHistoryImports.AsNoTracking().Where(item =>
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            item.LibraryScopeId == scope.LibraryScopeId).ToListAsync(cancellationToken);
        if (historyArtifacts != null)
            foreach (var import in imports) historyArtifacts.Delete(import.Id);
        var importJobIds = imports.Select(item => item.JobId).OfType<Guid>().ToHashSet();
        var historyJobs = await db.Jobs.Where(item =>
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.LibraryScopeId == scope.LibraryScopeId &&
            (item.Type == ListeningHistoryImportJobHandler.JobTypeName ||
             item.Type == MusicBrainzListeningEnrichmentQueue.JobType) &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled ||
             item.State == DurableJobState.Running)).ToListAsync(cancellationToken);
        foreach (var job in historyJobs.Where(item => importJobIds.Contains(item.Id) ||
                     item.Type == MusicBrainzListeningEnrichmentQueue.JobType && JobMatchesScope(item.PayloadJson, scope)))
        {
            job.CancellationRequestedAt ??= clock.UtcNow; job.UpdatedAt = clock.UtcNow; job.Revision++;
            if (job.State is DurableJobState.Pending or DurableJobState.RetryScheduled)
            { job.State = DurableJobState.Cancelled; job.CompletedAt = clock.UtcNow; }
        }
        var history = db.ListeningEvents.Where(item =>
            item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            item.LibraryScopeId == scope.LibraryScopeId);
        var occurrenceKeys = history.Select(item => item.OccurrenceKey);
        await db.PlaybackDeliveryCheckpoints.Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.OccurrenceKey != null && occurrenceKeys.Contains(item.OccurrenceKey))
            .ExecuteDeleteAsync(cancellationToken);
        await history.ExecuteDeleteAsync(cancellationToken);
        await db.ListeningHistoryImports.Where(item =>
                item.TenantId == scope.TenantId && item.OwnerUserId == scope.OwnerUserId &&
                item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
                item.LibraryScopeId == scope.LibraryScopeId)
            .ExecuteDeleteAsync(cancellationToken);
        var runs = await ScopedRuns(db, scope).ToListAsync(cancellationToken); var runIds = runs.Select(x => x.Id).ToArray();
        var jobIds = runs.Select(x => x.JobId).ToArray(); var jobs = await db.Jobs.Where(x => jobIds.Contains(x.Id)).ToListAsync(cancellationToken);
        foreach (var job in jobs.Where(x => x.State is DurableJobState.Pending or DurableJobState.RetryScheduled or DurableJobState.Running))
        {
            job.CancellationRequestedAt ??= clock.UtcNow; job.UpdatedAt = clock.UtcNow; job.Revision++;
            if (job.State is DurableJobState.Pending or DurableJobState.RetryScheduled) { job.State = DurableJobState.Cancelled; job.CompletedAt = clock.UtcNow; }
        }
        var runningJobIds = jobs.Where(x => x.State == DurableJobState.Running).Select(x => x.Id).ToHashSet();
        foreach (var run in runs.Where(x => runningJobIds.Contains(x.JobId))) { run.State = RecommendationRunState.Cancelled; run.CompletedAt = clock.UtcNow; run.UpdatedAt = clock.UtcNow; run.Revision++; }
        var schedules = await db.JobSchedules.Where(x => x.TenantId == scope.TenantId &&
            x.OwnerUserId == scope.OwnerUserId && x.LibraryScopeId == scope.LibraryScopeId &&
            x.JobType == DurableScheduleEngine.RecommendationJobType).ToListAsync(cancellationToken);
        foreach (var schedule in schedules)
        {
            RecommendationScheduleTemplate? template = null;
            try { template = JsonSerializer.Deserialize<RecommendationScheduleTemplate>(schedule.PayloadTemplateJson); }
            catch (JsonException) { }
            if (policy == null || template?.Version != 1 || template.IntelligencePolicyId != policy.Id) continue;
            schedule.Enabled = false; schedule.NextRunAt = null; schedule.UpdatedAt = clock.UtcNow; schedule.Revision++;
        }
        var scheduleIds = schedules.Where(schedule => !schedule.Enabled).Select(schedule => schedule.Id).ToHashSet();
        if (scheduleIds.Count > 0)
        {
            var childJobs = await db.Jobs.Where(job => job.TenantId == scope.TenantId &&
                job.OwnerUserId == scope.OwnerUserId && job.LibraryScopeId == scope.LibraryScopeId &&
                job.Type == "smart-playlist.materialize" &&
                (job.State == DurableJobState.Pending || job.State == DurableJobState.RetryScheduled ||
                 job.State == DurableJobState.Running)).ToListAsync(cancellationToken);
            foreach (var job in childJobs.Where(job => scheduleIds.Any(scheduleId =>
                         job.IdempotencyKey.StartsWith($"schedule:{scheduleId:N}:materialize:", StringComparison.Ordinal))))
            {
                job.CancellationRequestedAt ??= clock.UtcNow; job.UpdatedAt = clock.UtcNow; job.Revision++;
                if (job.State is DurableJobState.Pending or DurableJobState.RetryScheduled)
                { job.State = DurableJobState.Cancelled; job.CompletedAt = clock.UtcNow; }
            }
        }
        var sets = await db.GeneratedSets.Where(x => x.TenantId == scope.TenantId &&
            x.OwnerUserId == scope.OwnerUserId && x.Protocol == scope.Protocol &&
            x.BackendInstanceId == scope.BackendInstanceId && x.LibraryScopeId == scope.LibraryScopeId)
            .ToListAsync(cancellationToken); var setIds = sets.Select(x => x.Id).ToArray();
        if (setIds.Length > 0)
        {
            var setJobs = await db.Jobs.Where(job => job.TenantId == scope.TenantId &&
                job.OwnerUserId == scope.OwnerUserId && job.LibraryScopeId == scope.LibraryScopeId &&
                job.Type == "smart-playlist.materialize" &&
                (job.State == DurableJobState.Pending || job.State == DurableJobState.RetryScheduled ||
                 job.State == DurableJobState.Running)).ToListAsync(cancellationToken);
            // ponytail: purge is rare; add explicit job lineage only if scoped set volume makes this material.
            foreach (var job in setJobs.Where(job => setIds.Any(setId =>
                         job.IdempotencyKey == $"generated-set:{setId:N}" ||
                         job.IdempotencyKey.EndsWith($":materialize:{setId:N}", StringComparison.Ordinal))))
            {
                job.CancellationRequestedAt ??= clock.UtcNow; job.UpdatedAt = clock.UtcNow; job.Revision++;
                if (job.State is DurableJobState.Pending or DurableJobState.RetryScheduled)
                { job.State = DurableJobState.Cancelled; job.CompletedAt = clock.UtcNow; }
            }
        }
        db.GeneratedSetEntries.RemoveRange(db.GeneratedSetEntries.Where(x => setIds.Contains(x.GeneratedSetId)));
        db.GeneratedSets.RemoveRange(sets); db.RecommendationCandidates.RemoveRange(db.RecommendationCandidates.Where(x => runIds.Contains(x.RunId)));
        db.RecommendationRuns.RemoveRange(runs.Where(x => !runningJobIds.Contains(x.JobId))); db.ListeningProfiles.RemoveRange(ScopedProfiles(db, scope)); db.ListeningSignals.RemoveRange(ScopedSignals(db, scope));
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
    }
    private static async Task DisableSchedulesAsync(AllstarrDbContext db, IntelligenceScope scope,
        Guid policyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var schedules = await db.JobSchedules.Where(schedule => schedule.TenantId == scope.TenantId &&
            schedule.OwnerUserId == scope.OwnerUserId && schedule.LibraryScopeId == scope.LibraryScopeId &&
            schedule.JobType == DurableScheduleEngine.RecommendationJobType && schedule.Enabled)
            .ToListAsync(cancellationToken);
        foreach (var schedule in schedules)
        {
            RecommendationScheduleTemplate? template = null;
            try { template = JsonSerializer.Deserialize<RecommendationScheduleTemplate>(schedule.PayloadTemplateJson); }
            catch (JsonException) { }
            if (template?.Version != 1 || template.IntelligencePolicyId != policyId) continue;
            schedule.Enabled = false; schedule.NextRunAt = null; schedule.UpdatedAt = now; schedule.Revision++;
        }
    }
    internal static void ValidateScope(IntelligenceScope s)
    {
        if (s.TenantId == Guid.Empty || s.OwnerUserId == Guid.Empty ||
        s.Protocol is not ("jellyfin" or "subsonic") || string.IsNullOrWhiteSpace(s.BackendInstanceId) || string.IsNullOrWhiteSpace(s.LibraryScopeId)) throw new ArgumentException("The intelligence scope is invalid.");
    }
    internal static IQueryable<IntelligencePolicyRecord> Query(AllstarrDbContext db, IntelligenceScope s) => db.IntelligencePolicies.Where(x => x.TenantId == s.TenantId && x.OwnerUserId == s.OwnerUserId && x.Protocol == s.Protocol && x.BackendInstanceId == s.BackendInstanceId && x.LibraryScopeId == s.LibraryScopeId);
    internal static IQueryable<ListeningSignalRecord> ScopedSignals(AllstarrDbContext db, IntelligenceScope s) => db.ListeningSignals.Where(x => x.TenantId == s.TenantId && x.OwnerUserId == s.OwnerUserId && x.Protocol == s.Protocol && x.BackendInstanceId == s.BackendInstanceId && x.LibraryScopeId == s.LibraryScopeId);
    internal static IQueryable<ListeningProfileRecord> ScopedProfiles(AllstarrDbContext db, IntelligenceScope s) => db.ListeningProfiles.Where(x => x.TenantId == s.TenantId && x.OwnerUserId == s.OwnerUserId && x.Protocol == s.Protocol && x.BackendInstanceId == s.BackendInstanceId && x.LibraryScopeId == s.LibraryScopeId);
    internal static IQueryable<RecommendationRunRecord> ScopedRuns(AllstarrDbContext db, IntelligenceScope s) => db.RecommendationRuns.Where(x => x.TenantId == s.TenantId && x.OwnerUserId == s.OwnerUserId && x.Protocol == s.Protocol && x.BackendInstanceId == s.BackendInstanceId && x.LibraryScopeId == s.LibraryScopeId);
    private static bool JobMatchesScope(string payloadJson, IntelligenceScope scope)
    {
        try { return JsonSerializer.Deserialize<MusicBrainzListeningEnrichmentPayload>(payloadJson)?.Scope == scope; }
        catch (JsonException) { return false; }
    }
    internal static string Normalize(string value) { value = value?.Trim().ToLowerInvariant() ?? ""; if (value.Length is < 1 or > 100 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("An intelligence catalog value is invalid."); return value; }
}

public sealed class RecommendationSignalWriter(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock) : IIdempotentRecommendationSignalWriter
{
    public async Task<bool> WriteAsync(IntelligenceScope scope, string signalType, string trackKey, double value,
        DateTimeOffset observedAt, CancellationToken cancellationToken = default)
        => await WriteCoreAsync(scope, signalType, trackKey, value, observedAt, null, null, cancellationToken);

    public async Task<bool> WriteIdempotentAsync(IntelligenceScope scope, string signalType, string trackKey,
        double value, DateTimeOffset observedAt, string signalKey, Guid sourceJobId,
        CancellationToken cancellationToken = default) =>
        await WriteCoreAsync(scope, signalType, trackKey, value, observedAt, signalKey, sourceJobId, cancellationToken);

    private async Task<bool> WriteCoreAsync(IntelligenceScope scope, string signalType, string trackKey, double value,
        DateTimeOffset observedAt, string? signalKey, Guid? sourceJobId, CancellationToken cancellationToken)
    {
        IntelligencePolicyService.ValidateScope(scope); signalType = IntelligencePolicyService.Normalize(signalType);
        if (string.IsNullOrWhiteSpace(trackKey) || trackKey.Length > 500 || !double.IsFinite(value)) throw new ArgumentException("The recommendation signal is invalid.");
        if (signalKey != null && (signalKey.Length != 64 || signalKey.Any(character => !Uri.IsHexDigit(character))) || sourceJobId == Guid.Empty)
            throw new ArgumentException("The recommendation signal lineage is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var policy = await IntelligencePolicyService.Query(db, scope).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (policy == null || !policy.Enabled || !JsonSerializer.Deserialize<string[]>(policy.AllowedSignalTypesJson)!.Contains(signalType)) return false;
        var tracks = await db.LibraryTracks.AsNoTracking().Where(x => x.TenantId == scope.TenantId &&
            x.OwnerUserId == scope.OwnerUserId && x.BackendInstanceId == scope.BackendInstanceId &&
            x.LibraryScopeId == scope.LibraryScopeId).ToListAsync(cancellationToken);
        var track = tracks.SingleOrDefault(x => x.BackendItemId == trackKey || x.Id.ToString("D") == trackKey) ??
            tracks.FirstOrDefault(x => ProviderValueMatches(x.ProviderIdsJson, trackKey));
        if (track == null) return false;
        var expires = observedAt.AddDays(policy.RetentionDays); if (expires <= clock.UtcNow) return false;
        if (signalKey != null && await db.ListeningSignals.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId && x.OwnerUserId == scope.OwnerUserId && x.SignalKey == signalKey, cancellationToken)) return true;
        db.ListeningSignals.Add(new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = scope.TenantId,
            OwnerUserId = scope.OwnerUserId,
            Protocol = scope.Protocol,
            BackendInstanceId = scope.BackendInstanceId,
            LibraryScopeId = scope.LibraryScopeId,
            SignalType = signalType,
            TrackKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trackKey))),
            TrackReference = $"library:{track.Id:N}",
            SignalKey = signalKey,
            SourceJobId = sourceJobId,
            Value = value,
            ObservedAt = observedAt,
            ExpiresAt = expires
        });
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException) when (signalKey != null)
        {
            db.ChangeTracker.Clear();
            if (await db.ListeningSignals.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId && x.OwnerUserId == scope.OwnerUserId && x.SignalKey == signalKey, cancellationToken)) return true;
            throw;
        }
    }
    private static bool ProviderValueMatches(string json, string key)
    { try { var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json); return values?.Any(x => x.Value == key || $"{x.Key}:{x.Value}" == key) == true; } catch (JsonException) { return false; } }
}
