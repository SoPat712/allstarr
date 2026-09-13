using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record TrackRematchAllPreview(
    string ConfirmationId,
    string AlgorithmVersion,
    DateTimeOffset SnapshotCutoff,
    string SnapshotFingerprint,
    Guid? ScopeOwnerUserId,
    int TotalTracks,
    int ResolvedTracks,
    int ReviewTracks,
    int UnresolvedTracks,
    int ProtectedManualTracks,
    int AutomaticDecisionsToReplace,
    int TracksToRematch)
{
    public bool CanApply => TracksToRematch > 0;
}

public sealed record TrackRematchAllJobPayload(
    Guid OperationId,
    string TargetAlgorithmVersion,
    DateTimeOffset SnapshotCutoff,
    string SnapshotFingerprint,
    Guid? ScopeOwnerUserId,
    int ApprovedCount,
    bool Force);

public sealed class TrackRematchAllService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableJobQueue jobs,
    IPlatformClock clock)
{
    internal const string ForcePolicyVersion = "bulk-rematch-v1";
    internal const string RolloutPolicyVersion = "algorithm-rollout-v1";

    public async Task<TrackRematchAllPreview> PreviewAsync(
        Guid tenantId,
        Guid? scopeOwnerUserId,
        CancellationToken cancellationToken = default)
    {
        var cutoff = clock.UtcNow;
        var scope = await ReadScopeAsync(tenantId, scopeOwnerUserId, cutoff, cancellationToken);
        var eligible = scope.Groups.Where(item => !item.ProtectedManual).ToArray();
        return new(
            scope.ConfirmationId,
            TrackMatchDecisionEngine.AlgorithmVersion,
            scope.SnapshotCutoff,
            scope.SnapshotFingerprint,
            scopeOwnerUserId,
            scope.Groups.Count,
            scope.Groups.Count(item => item.Decision?.State is TrackMatchState.Accepted or TrackMatchState.Pinned),
            scope.Groups.Count(item => item.Decision?.State is TrackMatchState.Suggested or TrackMatchState.Ambiguous),
            scope.Groups.Count(item => item.Decision == null || item.Decision.State is
                TrackMatchState.Unresolved or TrackMatchState.Rejected),
            scope.Groups.Count(item => item.ProtectedManual),
            eligible.Count(item => item.Decision != null),
            eligible.Length);
    }

    public Task<DurableJobEnqueueResult> QueueForceAsync(
        Guid tenantId,
        Guid ownerUserId,
        TrackRematchAllPreview preview,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            tenantId,
            ownerUserId,
            $"manual:{preview.ConfirmationId}",
            preview.SnapshotCutoff,
            preview.SnapshotFingerprint,
            preview.ScopeOwnerUserId,
            preview.TracksToRematch,
            true,
            cancellationToken);

    public async Task<int> QueueAlgorithmUpgradesAsync(
        CancellationToken cancellationToken = default)
    {
        var cutoff = clock.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.ResourceKind == "track")
            .Select(item => new { item.TenantId, item.OwnerUserId })
            .Distinct()
            .ToArrayAsync(cancellationToken);
        var queued = 0;
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = await ReadScopeAsync(
                owner.TenantId, owner.OwnerUserId, cutoff, cancellationToken);
            var targets = scope.Groups.Where(item =>
                    !item.ProtectedManual &&
                    (item.Decision == null ||
                     item.Decision.MatcherVersion != TrackMatchDecisionEngine.AlgorithmVersion))
                .ToArray();
            if (targets.Length == 0) continue;
            var requestKey =
                $"algorithm:{TrackMatchDecisionEngine.AlgorithmVersion}:{scope.SnapshotFingerprint}";
            if (await db.Jobs.AsNoTracking().AnyAsync(item =>
                    item.TenantId == owner.TenantId &&
                    item.OwnerUserId == owner.OwnerUserId &&
                    item.Type == TrackRematchAllJobHandler.Type &&
                    item.IdempotencyKey == $"track-rematch-all:{requestKey}",
                    cancellationToken))
                continue;
            var result = await EnqueueAsync(
                owner.TenantId,
                owner.OwnerUserId,
                requestKey,
                scope.SnapshotCutoff,
                scope.SnapshotFingerprint,
                owner.OwnerUserId,
                targets.Length,
                false,
                cancellationToken);
            if (result.Created) queued++;
        }
        return queued;
    }

    internal async Task<TrackRematchWork> ReadWorkAsync(
        Guid tenantId,
        TrackRematchAllJobPayload payload,
        CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(
            tenantId, payload.ScopeOwnerUserId, payload.SnapshotCutoff, cancellationToken);
        var operationCorrelation = OperationCorrelation(payload.OperationId);
        if (!scope.SnapshotFingerprint.Equals(payload.SnapshotFingerprint, StringComparison.Ordinal))
            return new([], operationCorrelation, true);
        var skippedHashes = await ReadPermanentSkipsAsync(
            tenantId, operationCorrelation, cancellationToken);
        var targets = scope.Groups
            .Where(item => !item.ProtectedManual)
            .Where(item => payload.Force
                ? item.Decision?.CorrelationId != operationCorrelation ||
                  item.Decision.MatcherVersion != payload.TargetAlgorithmVersion
                : item.Decision == null ||
                  item.Decision.MatcherVersion != payload.TargetAlgorithmVersion)
            .Select(item => new TrackRematchWorkItem(
                item.Snapshot,
                RowHash(item.Snapshot.Id)))
            .Where(item => !skippedHashes.Contains(item.RowHash))
            .OrderBy(item => item.Snapshot.Id)
            .ToArray();
        return new(targets, operationCorrelation, false);
    }

    internal async Task<HashSet<Guid>> ReadProtectedOverrideIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> snapshotIds,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ManualTrackOverrides.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           snapshotIds.Contains(item.ExternalSnapshotId) &&
                           item.RevokedAt == null)
            .Select(item => item.ExternalSnapshotId)
            .ToHashSetAsync(cancellationToken);
    }

    private Task<DurableJobEnqueueResult> EnqueueAsync(
        Guid tenantId,
        Guid ownerUserId,
        string requestKey,
        DateTimeOffset snapshotCutoff,
        string snapshotFingerprint,
        Guid? scopeOwnerUserId,
        int approvedCount,
        bool force,
        CancellationToken cancellationToken)
    {
        var operationId = OperationId($"{tenantId:N}:{ownerUserId:N}:{requestKey}");
        return jobs.EnqueueAsync(new DurableJobEnqueueRequest<TrackRematchAllJobPayload>(
            TrackRematchAllJobHandler.Type,
            $"track-rematch-all:{requestKey}",
            new(
                operationId,
                TrackMatchDecisionEngine.AlgorithmVersion,
                snapshotCutoff,
                snapshotFingerprint,
                scopeOwnerUserId,
                approvedCount,
                force),
            tenantId,
            ownerUserId,
            MaxAttempts: 25,
            MaxDeferrals: 10_000,
            CorrelationId: OperationCorrelation(operationId)), cancellationToken);
    }

    private async Task<TrackRematchScope> ReadScopeAsync(
        Guid tenantId,
        Guid? ownerUserId,
        DateTimeOffset snapshotCutoff,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           (!ownerUserId.HasValue || item.OwnerUserId == ownerUserId.Value) &&
                           item.ResourceKind == "track" &&
                           item.RetrievedAt <= snapshotCutoff)
            .Select(item => new TrackRematchSnapshot(
                item.Id,
                item.OwnerUserId,
                item.ProviderTrackIdentityId,
                item.ProviderAccountId,
                item.ProviderId,
                item.ExternalIdHash,
                item.LibraryScopeId,
                item.Protocol,
                item.BackendInstanceId,
                item.BackendPrincipalId,
                item.SnapshotVersion,
                item.RetrievedAt))
            .ToArrayAsync(cancellationToken);
        if (snapshots.Length == 0)
            return new(Hash(string.Empty), DateTimeOffset.UnixEpoch, Hash(string.Empty), []);

        var snapshotGroups = snapshots
            .GroupBy(item => new TrackRematchSourceKey(
                item.OwnerUserId,
                item.LibraryScopeId,
                item.Protocol,
                item.BackendInstanceId,
                item.BackendPrincipalId,
                item.ProviderTrackIdentityId?.ToString("N") ??
                $"{item.ProviderId.ToLowerInvariant()}:{item.ExternalIdHash}"))
            .Select(group => new
            {
                Snapshots = group.ToArray(),
                Current = group.OrderByDescending(item => item.SnapshotVersion)
                    .ThenByDescending(item => item.RetrievedAt)
                    .ThenByDescending(item => item.Id)
                    .First()
            })
            .ToArray();
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisionQuery = db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId));
        var versions = decisionQuery.GroupBy(item => item.ExternalSnapshotId)
            .Select(group => new
            {
                ExternalSnapshotId = group.Key,
                DecisionVersion = group.Max(item => item.DecisionVersion)
            });
        var decisions = await decisionQuery.Join(
                versions,
                item => new { item.ExternalSnapshotId, item.DecisionVersion },
                version => new { version.ExternalSnapshotId, version.DecisionVersion },
                (item, _) => item)
            .ToArrayAsync(cancellationToken);
        var decisionsBySnapshot = decisions.ToDictionary(item => item.ExternalSnapshotId);
        var protectedSnapshots = await db.ManualTrackOverrides.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           snapshotIds.Contains(item.ExternalSnapshotId) &&
                           item.RevokedAt == null)
            .Select(item => item.ExternalSnapshotId)
            .ToHashSetAsync(cancellationToken);

        var identityIds = snapshots.Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var hashes = snapshots.Select(item => item.ExternalIdHash).Distinct().ToArray();
        var identities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           item.ResourceKind == ProviderResourceKind.Track &&
                           (identityIds.Contains(item.Id) || hashes.Contains(item.ExternalIdHash)))
            .ToArrayAsync(cancellationToken);
        var identitiesById = identities.ToDictionary(item => item.Id);

        var provisional = snapshotGroups.Select(group =>
        {
            var decision = group.Snapshots
                .Select(item => decisionsBySnapshot.GetValueOrDefault(item.Id))
                .Where(item => item != null)
                .OrderByDescending(item => item!.DecidedAt)
                .ThenByDescending(item => item!.DecisionVersion)
                .FirstOrDefault();
            var canonicalIds = group.Snapshots
                .Select(snapshot => SourceIdentity(snapshot, identitiesById, identities)?.CanonicalRecordingId)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .ToHashSet();
            if (decision?.CanonicalRecordingId is { } decisionCanonical)
                canonicalIds.Add(decisionCanonical);
            return new
            {
                group.Current,
                Decision = decision,
                SnapshotIds = group.Snapshots.Select(item => item.Id).ToArray(),
                CanonicalIds = canonicalIds
            };
        }).ToArray();
        var relevantCanonicalIds = provisional.SelectMany(item => item.CanonicalIds).Distinct().ToArray();
        var manuallyPinnedCanonicalIds = relevantCanonicalIds.Length == 0
            ? []
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.TenantId == tenantId &&
                               relevantCanonicalIds.Contains(item.CanonicalRecordingId) &&
                               item.Verification == ProviderIdentityVerification.Pinned &&
                               item.VerificationMethod == ManualTrackAuthorityPolicy.ProviderVerificationMethod)
                .Select(item => item.CanonicalRecordingId)
                .ToHashSetAsync(cancellationToken);
        var groups = provisional.Select(item => new TrackRematchGroup(
                item.Current,
                item.Decision,
                item.SnapshotIds.Any(protectedSnapshots.Contains) ||
                item.CanonicalIds.Any(manuallyPinnedCanonicalIds.Contains)))
            .ToArray();
        var snapshotFingerprint = Hash(string.Join('\n', groups
            .OrderBy(item => item.Snapshot.Id)
            .Select(item => $"{item.Snapshot.Id:N}:{item.Snapshot.SnapshotVersion}")));
        var confirmationId = Hash(string.Join('\n', groups
            .OrderBy(item => item.Snapshot.Id)
            .Select(item =>
                $"{item.Snapshot.Id:N}:{item.Snapshot.SnapshotVersion}:" +
                $"{item.Decision?.Id.ToString("N") ?? "none"}:" +
                $"{item.Decision?.DecisionVersion ?? 0}:{item.Decision?.Revision ?? 0}:" +
                item.ProtectedManual)));
        return new(confirmationId, snapshots.Max(item => item.RetrievedAt), snapshotFingerprint, groups);
    }

    private async Task<HashSet<string>> ReadPermanentSkipsAsync(
        Guid tenantId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var details = await db.AuditEvents.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           item.Category == "track-rematch" &&
                           item.CorrelationId == correlationId &&
                           item.Outcome.StartsWith("skipped_"))
            .Select(item => item.DetailsJson)
            .ToArrayAsync(cancellationToken);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in details)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("row", out var row) &&
                    row.GetString() is { Length: 24 } hash)
                    hashes.Add(hash);
            }
            catch (JsonException)
            {
            }
        }
        return hashes;
    }

    private static ProviderTrackIdentityRecord? SourceIdentity(
        TrackRematchSnapshot snapshot,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord> identitiesById,
        IReadOnlyCollection<ProviderTrackIdentityRecord> identities)
    {
        if (snapshot.ProviderTrackIdentityId is { } identityId &&
            identitiesById.TryGetValue(identityId, out var direct))
            return direct;
        return identities
            .Where(item => item.ProviderId.Equals(snapshot.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                           item.ExternalIdHash == snapshot.ExternalIdHash &&
                           (item.Scope == ProviderIdentityScope.Catalog ||
                            item.ProviderAccountId == snapshot.ProviderAccountId))
            .OrderByDescending(item => item.ProviderAccountId == snapshot.ProviderAccountId)
            .FirstOrDefault();
    }

    internal static string OperationCorrelation(Guid operationId) =>
        $"track-rematch:{operationId:N}";

    internal static string RowHash(Guid snapshotId) =>
        Convert.ToHexString(SHA256.HashData(snapshotId.ToByteArray()).AsSpan(0, 12)).ToLowerInvariant();

    internal static bool IsManagedPolicy(string policyVersion) =>
        policyVersion is ForcePolicyVersion or RolloutPolicyVersion;

    internal static bool RequiresAuthorityGuard(string policyVersion) =>
        IsManagedPolicy(policyVersion) ||
        policyVersion == ManualTrackAuthorityPolicy.RematchPolicyVersion;

    internal static AuditEventRecord SuccessAudit(
        Guid tenantId,
        Guid ownerUserId,
        Guid snapshotId,
        string correlationId,
        int decisionVersion,
        DateTimeOffset createdAt) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ActorUserId = ownerUserId,
            Category = "track-rematch",
            Action = "automatic.review",
            Outcome = "rematched",
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                row = RowHash(snapshotId),
                decisionVersion
            }),
            CreatedAt = createdAt
        };

    private static Guid OperationId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record TrackRematchSourceKey(
        Guid OwnerUserId,
        string LibraryScopeId,
        string Protocol,
        string BackendInstanceId,
        string BackendPrincipalId,
        string SourceIdentity);

    private sealed record TrackRematchGroup(
        TrackRematchSnapshot Snapshot,
        TrackMatchRecord? Decision,
        bool ProtectedManual);

    private sealed record TrackRematchScope(
        string ConfirmationId,
        DateTimeOffset SnapshotCutoff,
        string SnapshotFingerprint,
        IReadOnlyList<TrackRematchGroup> Groups);
}

internal sealed record TrackRematchSnapshot(
    Guid Id,
    Guid OwnerUserId,
    Guid? ProviderTrackIdentityId,
    Guid ProviderAccountId,
    string ProviderId,
    string ExternalIdHash,
    string LibraryScopeId,
    string Protocol,
    string BackendInstanceId,
    string BackendPrincipalId,
    int SnapshotVersion,
    DateTimeOffset RetrievedAt);

internal sealed record TrackRematchWorkItem(
    TrackRematchSnapshot Snapshot,
    string RowHash);

internal sealed record TrackRematchWork(
    IReadOnlyList<TrackRematchWorkItem> Pending,
    string OperationCorrelation,
    bool ScopeChanged);

public sealed class TrackRematchAllJobHandler(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    TrackRematchAllService rematches,
    ITrackMatchRepository trackMatches,
    IPlatformClock clock) : IDurableJobHandler
{
    public const string Type = "track-match.rematch-all";
    private const int BatchSize = 25;

    public string JobType => Type;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        TrackRematchAllJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<TrackRematchAllJobPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null ||
            payload.OperationId == Guid.Empty ||
            payload.SnapshotCutoff == default ||
            payload.SnapshotFingerprint is not { Length: 64 } ||
            payload.ApprovedCount < 1 ||
            payload.TargetAlgorithmVersion != TrackMatchDecisionEngine.AlgorithmVersion ||
            !context.Claim.TenantId.HasValue ||
            !context.Claim.OwnerUserId.HasValue)
            return DurableJobCompletion.Failure(
                "track_rematch_payload_invalid",
                "The rematch request is invalid or targets a retired matching algorithm.");

        var tenantId = context.Claim.TenantId.Value;
        var work = await rematches.ReadWorkAsync(tenantId, payload, cancellationToken);
        if (work.ScopeChanged)
            return DurableJobCompletion.Failure(
                "track_rematch_scope_changed",
                "The confirmed snapshot scope changed before the rematch finished; queue a new preview.");
        if (work.Pending.Count == 0)
            return DurableJobCompletion.Success();

        var batch = work.Pending.Take(BatchSize).ToArray();
        var runtimes = await LoadRuntimesAsync(
            tenantId,
            batch.Select(item => item.Snapshot.OwnerUserId).Distinct().ToArray(),
            cancellationToken);
        var protectedIds = await rematches.ReadProtectedOverrideIdsAsync(
            tenantId, batch.Select(item => item.Snapshot.Id).ToArray(), cancellationToken);
        var completed = Math.Max(0, payload.ApprovedCount - work.Pending.Count);
        var total = Math.Max(payload.ApprovedCount, completed + work.Pending.Count);
        var started = Stopwatch.GetTimestamp();
        var transientFailures = 0;
        var audits = new List<AuditEventRecord>(batch.Length);

        foreach (var item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (protectedIds.Contains(item.Snapshot.Id))
            {
                audits.Add(Audit(context, item, "skipped_manual", null));
                completed++;
                continue;
            }

            var execution = runtimes.TryGetValue(item.Snapshot.OwnerUserId, out var runtime)
                ? CreateExecution(context, runtime, item.Snapshot, cancellationToken)
                : null;
            if (execution == null)
            {
                audits.Add(Audit(context, item, "retry_identity_unavailable", null));
                transientFailures++;
                continue;
            }

            try
            {
                var result = await trackMatches.RematchSnapshotAsync(
                    execution,
                    item.Snapshot.Id,
                    work.OperationCorrelation,
                    payload.Force
                        ? TrackRematchAllService.ForcePolicyVersion
                        : TrackRematchAllService.RolloutPolicyVersion,
                    cancellationToken);
                var permanent = result.Failure is TrackMatchCommandFailure.Invalid or
                    TrackMatchCommandFailure.NotFound or TrackMatchCommandFailure.Forbidden;
                if (!result.Succeeded)
                    audits.Add(Audit(
                        context,
                        item,
                        permanent
                            ? $"skipped_{result.Failure.ToString().ToLowerInvariant()}"
                            : "retry_conflict",
                        null));
                if (result.Succeeded || permanent) completed++;
                else transientFailures++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                audits.Add(Audit(context, item, "retry_exception", null));
                transientFailures++;
            }
        }

        await SaveAuditsAsync(audits, cancellationToken);
        var remaining = Math.Max(0, total - completed);
        await context.ReportProgressAsync(new(
            "track.rematch",
            remaining == 0
                ? "Finished replacing automatic match decisions."
                : $"Reprocessed {completed} of {total} automatic match decisions.",
            completed,
            total,
            ThroughputPerSecond: batch.Length /
                Math.Max(Stopwatch.GetElapsedTime(started).TotalSeconds, .001)), cancellationToken);

        if (transientFailures > 0)
            return DurableJobCompletion.Retry(
                "track_rematch_rows_failed",
                $"{transientFailures} track decisions could not be rematched and will be retried.",
                TimeSpan.FromMinutes(1));
        return remaining == 0
            ? DurableJobCompletion.Success()
            : DurableJobCompletion.Defer(
                "track_rematch_batch_pending",
                "The next bounded rematch batch is scheduled.",
                TimeSpan.FromSeconds(2));
    }

    private async Task<IReadOnlyDictionary<Guid, RuntimeIdentity>> LoadRuntimesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> ownerUserIds,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var users = await db.Users.AsNoTracking()
            .Where(item => item.TenantId == tenantId && ownerUserIds.Contains(item.Id))
            .ToArrayAsync(cancellationToken);
        var identities = await db.BackendIdentities.AsNoTracking()
            .Where(item => item.TenantId == tenantId && ownerUserIds.Contains(item.UserId))
            .OrderByDescending(item => item.LastSeenAt)
            .ToArrayAsync(cancellationToken);
        var identitiesByUser = identities.GroupBy(item => item.UserId)
            .ToDictionary(group => group.Key, group => group
                .GroupBy(item => IdentityKey(
                    item.BackendType, item.BackendInstanceId, item.PrincipalId))
                .ToDictionary(values => values.Key, values => values.First().PrincipalId));
        return users.ToDictionary(
            user => user.Id,
            user => new RuntimeIdentity(
                user.DisplayName,
                identitiesByUser.GetValueOrDefault(user.Id) ?? new Dictionary<string, string>()));
    }

    private ProtocolExecutionContext? CreateExecution(
        DurableJobExecutionContext context,
        RuntimeIdentity runtime,
        TrackRematchSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var protocolName = snapshot.Protocol.Trim().ToLowerInvariant();
        var protocol = protocolName switch
        {
            "jellyfin" => ProtocolKind.Jellyfin,
            "subsonic" or "opensubsonic" => ProtocolKind.Subsonic,
            _ => (ProtocolKind?)null
        };
        if (!protocol.HasValue ||
            !runtime.PrincipalIds.TryGetValue(IdentityKey(
                protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic",
                snapshot.BackendInstanceId,
                snapshot.BackendPrincipalId),
                out var principalId))
            return null;
        var tenantId = context.Claim.TenantId!.Value;
        return new(
            protocol.Value,
            snapshot.BackendInstanceId,
            principalId,
            new AllstarrPrincipal(
                tenantId,
                snapshot.OwnerUserId,
                protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic",
                snapshot.BackendInstanceId,
                principalId,
                runtime.DisplayName,
                false),
            context.Claim.CorrelationId,
            clock.UtcNow.AddMinutes(2),
            cancellationToken,
            libraryScopeId: snapshot.LibraryScopeId);
    }

    private static string IdentityKey(
        string backendType,
        string backendInstanceId,
        string principalId) =>
        $"{backendType.Trim().ToLowerInvariant()}\n{backendInstanceId}\n{principalId}";

    private AuditEventRecord Audit(
        DurableJobExecutionContext context,
        TrackRematchWorkItem item,
        string outcome,
        int? decisionVersion) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.Claim.TenantId,
            ActorUserId = context.Claim.OwnerUserId,
            Category = "track-rematch",
            Action = "automatic.review",
            Outcome = outcome,
            CorrelationId = context.Claim.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                row = item.RowHash,
                decisionVersion
            }),
            CreatedAt = clock.UtcNow
        };

    private async Task SaveAuditsAsync(
        IReadOnlyCollection<AuditEventRecord> audits,
        CancellationToken cancellationToken)
    {
        if (audits.Count == 0) return;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.AddRange(audits);
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record RuntimeIdentity(
        string DisplayName,
        IReadOnlyDictionary<string, string> PrincipalIds);
}

public sealed class TrackMatchAlgorithmRolloutService(
    TrackRematchAllService rematches,
    DurableStorageState storageState,
    ILogger<TrackMatchAlgorithmRolloutService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            try
            {
                var queued = await rematches.QueueAlgorithmUpgradesAsync(stoppingToken);
                if (queued > 0)
                    logger.LogInformation(
                        "Queued {Count} gradual matching algorithm rollout jobs for {AlgorithmVersion}",
                        queued,
                        TrackMatchDecisionEngine.AlgorithmVersion);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Matching algorithm rollout scan failed ({FailureKind}); retrying",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
