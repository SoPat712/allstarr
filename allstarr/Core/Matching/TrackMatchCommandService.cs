using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Spotify;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record TrackMatchActor(Guid TenantId, Guid UserId, bool IsAdministrator);

public sealed record ResolveTrackMatchCommand(
    string TargetType,
    Guid? LibraryTrackId = null,
    string? BackendItemId = null,
    string? ExternalProvider = null,
    string? ExternalId = null,
    string? Reason = null);

public sealed record PersistAutomatedTrackMatchCommand(
    string TargetType,
    string TargetId,
    string? ExternalProvider = null,
    double Confidence = 1);

public sealed record DurableProviderRoute(string ProviderId, string ExternalId, bool IsManual = false);

public sealed record DurableSpotifyMatchProjection(
    string? LocalBackendItemId,
    bool LocalIsManual,
    IReadOnlyList<DurableProviderRoute> ProviderRoutes)
{
    public bool IsManual => LocalIsManual || ProviderRoutes.Any(route => route.IsManual);
}

public sealed record TrackMatchDetailData(
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities,
    IReadOnlyList<LibraryTrackRecord> LocalTracks,
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<TrackMatchRecord> Decisions,
    IReadOnlyList<ManualTrackOverrideRecord> Overrides,
    IReadOnlyList<ProviderDownloadArtifactEntity> Artifacts);

public sealed record TrackMatchReviewData(
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<TrackMatchRecord> LatestDecisions,
    IReadOnlyList<ManualTrackOverrideRecord> ActiveOverrides,
    IReadOnlyList<LibraryTrackRecord> LibraryTracks,
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities);

public sealed record TrackMatchActivityData(
    IReadOnlyList<TrackMatchRecord> Decisions,
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities,
    IReadOnlyList<LibraryTrackRecord> LibraryTracks);

public sealed record TrackMatchResolutionData(
    IReadOnlyList<ExternalMetadataSnapshotRecord> Snapshots,
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities,
    IReadOnlyList<ManualTrackOverrideRecord> ActiveOverrides,
    IReadOnlyList<TrackMatchRecord> LatestDecisions);

public sealed record SourceTrackSeed(
    string ProviderId,
    string ExternalId,
    string Title,
    string Artist,
    string? Album,
    int? DurationMilliseconds,
    string? Isrc,
    string? ArtworkReference,
    string ProviderRevision);

public enum TrackMatchCommandFailure
{
    None,
    Invalid,
    NotFound,
    Forbidden,
    Conflict
}

public sealed record TrackMatchCommandResult(
    bool Succeeded,
    TrackMatchCommandFailure Failure = TrackMatchCommandFailure.None,
    string? Error = null,
    Guid? ExternalSnapshotId = null)
{
    public static TrackMatchCommandResult Success(Guid snapshotId) => new(true, ExternalSnapshotId: snapshotId);
    public static TrackMatchCommandResult Fail(TrackMatchCommandFailure failure, string error) => new(false, failure, error);
}

public sealed record TrackRematchCommandResult(
    bool Succeeded,
    TrackMatchCommandFailure Failure = TrackMatchCommandFailure.None,
    string? Error = null,
    string? State = null,
    double Confidence = 0,
    int CandidateCount = 0,
    int DecisionVersion = 0);

public interface ITrackMatchRepository
{
    Task<int> EnsureSourceSnapshotsAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        CancellationToken cancellationToken = default);

    Task<DurableSpotifyMatchProjection> GetSpotifyProjectionAsync(
        string spotifyId,
        CancellationToken cancellationToken = default);

    Task<TrackMatchDetailData> GetDetailAsync(
        TrackMatchActor actor,
        string providerId,
        string externalId,
        string? backendItemId = null,
        CancellationToken cancellationToken = default);

    Task<TrackMatchReviewData> GetReviewDataAsync(
        TrackMatchActor actor,
        string? libraryScopeId = null,
        string? search = null,
        int scanLimit = 5000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryTrackRecord>> SearchLocalTracksAsync(
        TrackMatchActor actor,
        string query,
        string? libraryScopeId = null,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<TrackMatchActivityData> GetActivityDataAsync(
        TrackMatchActor actor,
        DateTimeOffset? before = null,
        Guid? beforeId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<TrackMatchResolutionData> GetResolutionDataAsync(
        TrackMatchActor actor,
        Guid ownerUserId,
        string? libraryScopeId,
        IReadOnlyCollection<Guid> externalSnapshotIds,
        CancellationToken cancellationToken = default);

    Task<ExternalMetadataSnapshotRecord> CaptureSnapshotAsync(
        ProtocolExecutionContext context,
        ExternalSnapshotInput input,
        CancellationToken cancellationToken = default);

    Task<TrackMatchRecord> RecordDecisionAsync(
        ProtocolExecutionContext context,
        MatchDecisionInput input,
        CancellationToken cancellationToken = default);

    Task<ManualTrackOverrideRecord> SetOverrideAsync(
        ProtocolExecutionContext context,
        ManualOverrideInput input,
        CancellationToken cancellationToken = default);

    Task RevokeOverrideAsync(
        ProtocolExecutionContext context,
        Guid overrideId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<ManualTrackOverrideRecord?> GetActiveOverrideAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default);

    Task<ExternalMetadataSnapshotRecord?> FindSnapshotAsync(
        Guid tenantId,
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default);

    Task<ManualTrackOverrideRecord?> FindOverrideAsync(
        Guid tenantId,
        Guid overrideId,
        CancellationToken cancellationToken = default);

    Task<int> PersistAutomatedSpotifyAsync(
        string spotifyId,
        PersistAutomatedTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackRematchCommandResult> RematchSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackMatchCommandResult> ClearSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackMatchCommandResult> ResolveSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackMatchCommandResult> ResolveSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public sealed class TrackMatchCommandService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    TrackMatchDecisionEngine decisionEngine,
    ProviderAccountResolver accountResolver,
    IPlatformClock clock) : ITrackMatchRepository
{
    public async Task<ExternalMetadataSnapshotRecord> CaptureSnapshotAsync(
        ProtocolExecutionContext context,
        ExternalSnapshotInput input,
        CancellationToken cancellationToken = default)
    {
        var (principal, actor) = PersistenceGuard.Require(context, input.LibraryScopeId);
        ValidateHash(input.ExternalIdHash, nameof(input.ExternalIdHash));
        ValidateHash(input.PayloadSha256, nameof(input.PayloadSha256));
        if (input.SnapshotVersion <= 0 ||
            string.IsNullOrWhiteSpace(input.ProviderRevision) ||
            string.IsNullOrWhiteSpace(input.ResourceKind))
            throw new ArgumentException(
                "Snapshot version, provider revision, and resource kind are required.",
                nameof(input));
        PersistenceGuard.ValidateSafeJson(input.PayloadJson, nameof(input.PayloadJson));
        var account = await accountResolver.ResolveAsync(new ProviderAccountResolutionRequest(
                principal,
                input.ProviderId,
                "metadata",
                input.ProviderAccountId,
                input.LibraryScopeId),
            cancellationToken) ?? throw new UnauthorizedAccessException("The provider account is unavailable.");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId &&
            item.ProviderAccountId == account.Account.Id &&
            item.ResourceKind == input.ResourceKind &&
            item.ExternalIdHash == input.ExternalIdHash &&
            item.SnapshotVersion == input.SnapshotVersion,
            cancellationToken);
        if (existing != null)
        {
            if (!existing.PayloadSha256.Equals(input.PayloadSha256, StringComparison.Ordinal) ||
                existing.OwnerUserId != actor.EffectiveUserId ||
                existing.LibraryScopeId != input.LibraryScopeId)
                throw new InvalidOperationException(
                    "The snapshot version already exists with different immutable content or scope.");
            return existing;
        }

        var record = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = actor.EffectiveUserId!.Value,
            ProviderAccountId = account.Account.Id,
            ProviderTrackIdentityId = input.ProviderTrackIdentityId,
            SourceJobId = input.SourceJobId,
            LibraryScopeId = input.LibraryScopeId,
            BackendInstanceId = context.BackendInstanceId,
            BackendPrincipalId = context.VerifiedBackendPrincipalId,
            Protocol = context.Protocol.ToString().ToLowerInvariant(),
            ProviderId = account.Account.ProviderId,
            ResourceKind = input.ResourceKind.Trim().ToLowerInvariant(),
            ExternalIdHash = input.ExternalIdHash,
            SnapshotVersion = input.SnapshotVersion,
            ProviderRevision = input.ProviderRevision.Trim(),
            PayloadJson = input.PayloadJson,
            PayloadSha256 = input.PayloadSha256,
            CorrelationId = context.CorrelationId,
            RetrievedAt = clock.UtcNow
        };
        db.ExternalMetadataSnapshots.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task<TrackMatchRecord> RecordDecisionAsync(
        ProtocolExecutionContext context,
        MatchDecisionInput input,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        if (input.DecisionVersion <= 0 ||
            input.Confidence is < 0 or > 1 ||
            input.Threshold is < 0 or > 1 ||
            string.IsNullOrWhiteSpace(input.PolicyVersion))
            throw new ArgumentException("The match decision is incomplete.", nameof(input));
        PersistenceGuard.ValidateSafeJson(input.CandidateResultsJson, nameof(input.CandidateResultsJson));
        PersistenceGuard.ValidateSafeJson(input.ReasonsJson, nameof(input.ReasonsJson));
        PersistenceGuard.ValidateSafeJson(input.WarningsJson, nameof(input.WarningsJson));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshotAsync(db, actor, input.ExternalSnapshotId, cancellationToken);
        PersistenceGuard.RequireLibrary(context, snapshot.LibraryScopeId);
        if (input.State is TrackMatchState.Accepted or TrackMatchState.Pinned &&
                !input.LibraryTrackId.HasValue ||
            input.State is TrackMatchState.Unresolved or TrackMatchState.Suggested or
                TrackMatchState.Rejected or TrackMatchState.Ambiguous &&
                input.LibraryTrackId.HasValue)
            throw new ArgumentException(
                "The selected library track does not match the decision state.",
                nameof(input));
        if (input.State == TrackMatchState.Accepted && input.Confidence < input.Threshold)
            throw new ArgumentException(
                "A match below its acceptance threshold cannot be accepted for automatic action.",
                nameof(input));
        if (input.LibraryTrackId.HasValue &&
            !await db.LibraryTracks.AnyAsync(item =>
                    item.Id == input.LibraryTrackId &&
                    item.TenantId == actor.TenantId &&
                    item.OwnerUserId == snapshot.OwnerUserId &&
                    item.LibraryScopeId == snapshot.LibraryScopeId,
                cancellationToken))
            throw new UnauthorizedAccessException(
                "The selected library track is outside the snapshot scope.");

        var existing = await db.TrackMatches.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == snapshot.OwnerUserId &&
            item.LibraryScopeId == snapshot.LibraryScopeId &&
            item.ExternalSnapshotId == snapshot.Id &&
            item.DecisionVersion == input.DecisionVersion,
            cancellationToken);
        if (existing != null)
        {
            if (!MatchesImmutableDecision(existing, input))
                throw new InvalidOperationException(
                    "The match decision version already exists with different content.");
            return existing;
        }

        var record = new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = snapshot.OwnerUserId,
            ExternalSnapshotId = snapshot.Id,
            LibraryTrackId = input.LibraryTrackId,
            CanonicalRecordingId = input.CanonicalRecordingId,
            LibraryScopeId = snapshot.LibraryScopeId,
            State = input.State,
            Confidence = input.Confidence,
            Threshold = input.Threshold,
            DecisionVersion = input.DecisionVersion,
            PolicyVersion = input.PolicyVersion.Trim(),
            CandidateResultsJson = input.CandidateResultsJson,
            ReasonsJson = input.ReasonsJson,
            WarningsJson = input.WarningsJson,
            CorrelationId = context.CorrelationId,
            DecidedAt = clock.UtcNow
        };
        db.TrackMatches.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return record;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.TrackMatches.AsNoTracking().SingleOrDefaultAsync(item =>
                item.TenantId == actor.TenantId &&
                item.OwnerUserId == snapshot.OwnerUserId &&
                item.LibraryScopeId == snapshot.LibraryScopeId &&
                item.ExternalSnapshotId == snapshot.Id &&
                item.DecisionVersion == input.DecisionVersion,
                cancellationToken);
            if (winner is null)
            {
                throw;
            }

            if (!MatchesImmutableDecision(winner, input))
                throw new InvalidOperationException(
                    "A concurrent match decision used the same version with different content.");
            return winner;
        }
    }

    public async Task<ManualTrackOverrideRecord> SetOverrideAsync(
        ProtocolExecutionContext context,
        ManualOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        PersistenceGuard.RequireLibrary(context, input.LibraryScopeId);
        if (string.IsNullOrWhiteSpace(input.Reason) ||
            input.Decision == ManualOverrideDecision.Pin != input.LibraryTrackId.HasValue)
            throw new ArgumentException(
                "Pin requires a local track; Reject must not select one.",
                nameof(input));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshotAsync(db, actor, input.ExternalSnapshotId, cancellationToken);
        if (snapshot.LibraryScopeId != input.LibraryScopeId)
            throw new UnauthorizedAccessException(
                "The override library scope does not match the snapshot.");
        if (input.LibraryTrackId.HasValue &&
            !await db.LibraryTracks.AnyAsync(item =>
                    item.Id == input.LibraryTrackId &&
                    item.TenantId == actor.TenantId &&
                    item.OwnerUserId == snapshot.OwnerUserId &&
                    item.LibraryScopeId == input.LibraryScopeId,
                cancellationToken))
            throw new UnauthorizedAccessException(
                "The pinned library track is outside the override scope.");

        var active = await db.ManualTrackOverrides.SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == snapshot.OwnerUserId &&
            item.LibraryScopeId == input.LibraryScopeId &&
            item.ExternalSnapshotId == snapshot.Id &&
            item.RevokedAt == null,
            cancellationToken);
        var version = await db.ManualTrackOverrides
            .Where(item =>
                item.TenantId == actor.TenantId &&
                item.OwnerUserId == snapshot.OwnerUserId &&
                item.ExternalSnapshotId == snapshot.Id)
            .Select(item => (int?)item.DecisionVersion)
            .MaxAsync(cancellationToken) ?? 0;
        if (active != null)
        {
            if (active.Decision == input.Decision &&
                active.LibraryTrackId == input.LibraryTrackId &&
                active.Reason == input.Reason.Trim())
                return active;
            active.RevokedAt = clock.UtcNow;
            active.Revision++;
            await db.SaveChangesAsync(cancellationToken);
        }

        var record = new ManualTrackOverrideRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = snapshot.OwnerUserId,
            ExternalSnapshotId = snapshot.Id,
            LibraryTrackId = input.LibraryTrackId,
            LibraryScopeId = input.LibraryScopeId,
            Decision = input.Decision,
            Reason = input.Reason.Trim(),
            DecisionVersion = version + 1,
            CreatedAt = clock.UtcNow
        };
        db.ManualTrackOverrides.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task RevokeOverrideAsync(
        ProtocolExecutionContext context,
        Guid overrideId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.ManualTrackOverrides.SingleOrDefaultAsync(item =>
            item.Id == overrideId && item.TenantId == actor.TenantId,
            cancellationToken) ?? throw new KeyNotFoundException("Override not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId);
        PersistenceGuard.RequireLibrary(context, record.LibraryScopeId);
        if (record.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException(
                "The override changed before revocation.");
        if (record.RevokedAt != null) return;
        record.RevokedAt = clock.UtcNow;
        record.Revision++;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ManualTrackOverrideRecord?> GetActiveOverrideAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await OwnedSnapshotAsync(db, actor, externalSnapshotId, cancellationToken);
        PersistenceGuard.RequireLibrary(context, snapshot.LibraryScopeId);
        return await db.ManualTrackOverrides.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == snapshot.OwnerUserId &&
            item.LibraryScopeId == snapshot.LibraryScopeId &&
            item.ExternalSnapshotId == snapshot.Id &&
            item.RevokedAt == null,
            cancellationToken);
    }

    public async Task<ExternalMetadataSnapshotRecord?> FindSnapshotAsync(
        Guid tenantId,
        Guid externalSnapshotId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExternalMetadataSnapshots.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId && item.TenantId == tenantId,
            cancellationToken);
    }

    public async Task<ManualTrackOverrideRecord?> FindOverrideAsync(
        Guid tenantId,
        Guid overrideId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ManualTrackOverrides.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == overrideId && item.TenantId == tenantId,
            cancellationToken);
    }

    public async Task<TrackMatchDetailData> GetDetailAsync(
        TrackMatchActor actor,
        string providerId,
        string externalId,
        string? backendItemId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        providerId = providerId.Trim().ToLowerInvariant();
        externalId = externalId.Trim();

        var sourceIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ProviderId == providerId &&
                           item.ExternalId == externalId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var canonicalIds = sourceIdentities
            .Select(item => item.CanonicalRecordingId)
            .Distinct()
            .ToArray();
        var identities = canonicalIds.Length == 0
            ? sourceIdentities
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               canonicalIds.Contains(item.CanonicalRecordingId))
                .OrderBy(item => item.ProviderId)
                .ThenBy(item => item.CreatedAt)
                .ToListAsync(cancellationToken);

        var localQuery = db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId);
        if (!actor.IsAdministrator)
            localQuery = localQuery.Where(item => item.OwnerUserId == actor.UserId);
        var localTracks = await localQuery
            .Where(item =>
                item.CanonicalRecordingId.HasValue &&
                canonicalIds.Contains(item.CanonicalRecordingId.Value) ||
                backendItemId != null && item.BackendItemId == backendItemId)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);

        var identityIds = identities.Select(item => item.Id).Distinct().ToArray();
        var snapshotQuery = db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value));
        if (!actor.IsAdministrator)
            snapshotQuery = snapshotQuery.Where(item => item.OwnerUserId == actor.UserId);
        var snapshots = await snapshotQuery.OrderByDescending(item => item.RetrievedAt)
            .ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisions = snapshotIds.Length == 0
            ? []
            : await db.TrackMatches.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               snapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.DecidedAt)
                .ToListAsync(cancellationToken);
        var overrides = snapshotIds.Length == 0
            ? []
            : await db.ManualTrackOverrides.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               snapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync(cancellationToken);

        var externalIds = identities.Select(item => item.ExternalId)
            .Append(externalId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct()
            .ToArray();
        var artifactQuery = db.ProviderDownloadArtifacts.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           externalIds.Contains(item.ProviderArtifactId));
        if (!actor.IsAdministrator)
            artifactQuery = artifactQuery.Where(item =>
                item.OwnerUserId == null || item.OwnerUserId == actor.UserId);
        var artifacts = await artifactQuery.OrderByDescending(item => item.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return new(identities, localTracks, snapshots, decisions, overrides, artifacts);
    }

    public async Task<TrackMatchReviewData> GetReviewDataAsync(
        TrackMatchActor actor,
        string? libraryScopeId = null,
        string? search = null,
        int scanLimit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshotsQuery = db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId);
        if (!actor.IsAdministrator)
            snapshotsQuery = snapshotsQuery.Where(item => item.OwnerUserId == actor.UserId);
        if (!string.IsNullOrWhiteSpace(libraryScopeId))
            snapshotsQuery = snapshotsQuery.Where(item => item.LibraryScopeId == libraryScopeId.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            snapshotsQuery = snapshotsQuery.Where(item =>
                EF.Functions.ILike(item.ProviderId, pattern, "\\") ||
                EF.Functions.ILike(item.PayloadJson, pattern, "\\"));
        }

        var snapshots = await snapshotsQuery.OrderByDescending(item => item.RetrievedAt)
            .Take(Math.Clamp(scanLimit, 1, 10000))
            .ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisionQuery = db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           snapshotIds.Contains(item.ExternalSnapshotId));
        var latestVersions = decisionQuery
            .GroupBy(item => item.ExternalSnapshotId)
            .Select(group => new
            {
                ExternalSnapshotId = group.Key,
                DecisionVersion = group.Max(item => item.DecisionVersion)
            });
        var decisions = await decisionQuery.Join(
                latestVersions,
                item => new { item.ExternalSnapshotId, item.DecisionVersion },
                latest => new { latest.ExternalSnapshotId, latest.DecisionVersion },
                (item, _) => item)
            .ToListAsync(cancellationToken);
        var overrides = await db.ManualTrackOverrides.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           snapshotIds.Contains(item.ExternalSnapshotId) &&
                           item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var libraryIds = decisions.Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Concat(overrides.Where(item => item.LibraryTrackId.HasValue)
                .Select(item => item.LibraryTrackId!.Value))
            .Distinct()
            .ToArray();
        var library = await db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && libraryIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var canonicalIds = decisions.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)
            .Concat(library.Where(item => item.CanonicalRecordingId.HasValue)
                .Select(item => item.CanonicalRecordingId!.Value))
            .Distinct()
            .ToArray();
        var identities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           canonicalIds.Contains(item.CanonicalRecordingId))
            .OrderBy(item => item.ProviderId)
            .ThenBy(item => item.ExternalId)
            .ToListAsync(cancellationToken);
        return new(snapshots, decisions, overrides, library, identities);
    }

    public async Task<IReadOnlyList<LibraryTrackRecord>> SearchLocalTracksAsync(
        TrackMatchActor actor,
        string query,
        string? libraryScopeId = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pattern = $"%{query.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
        var tracks = db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId);
        if (!actor.IsAdministrator)
            tracks = tracks.Where(item => item.OwnerUserId == actor.UserId);
        if (!string.IsNullOrWhiteSpace(libraryScopeId))
            tracks = tracks.Where(item => item.LibraryScopeId == libraryScopeId.Trim());
        return await tracks
            .Where(item =>
                EF.Functions.ILike(item.Title, pattern, "\\") ||
                EF.Functions.ILike(item.Artist, pattern, "\\") ||
                item.Album != null && EF.Functions.ILike(item.Album, pattern, "\\"))
            .OrderBy(item => item.Artist)
            .ThenBy(item => item.Title)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken);
    }

    public async Task<TrackMatchActivityData> GetActivityDataAsync(
        TrackMatchActor actor,
        DateTimeOffset? before = null,
        Guid? beforeId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var matches = await db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           (!before.HasValue ||
                            item.DecidedAt < before.Value ||
                            item.DecidedAt == before.Value &&
                            beforeId.HasValue &&
                            item.Id.CompareTo(beforeId.Value) < 0))
            .OrderByDescending(item => item.DecidedAt)
            .ThenByDescending(item => item.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        var snapshotIds = matches.Select(item => item.ExternalSnapshotId).Distinct().ToArray();
        var snapshots = snapshotIds.Length == 0
            ? []
            : await db.ExternalMetadataSnapshots.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               snapshotIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        var identityIds = snapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var identities = identityIds.Length == 0
            ? []
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               identityIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        var libraryIds = matches
            .Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Distinct()
            .ToArray();
        var libraryTracks = libraryIds.Length == 0
            ? []
            : await db.LibraryTracks.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               libraryIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        return new(matches, snapshots, identities, libraryTracks);
    }

    public async Task<TrackMatchResolutionData> GetResolutionDataAsync(
        TrackMatchActor actor,
        Guid ownerUserId,
        string? libraryScopeId,
        IReadOnlyCollection<Guid> externalSnapshotIds,
        CancellationToken cancellationToken = default)
    {
        var snapshotIds = externalSnapshotIds.Distinct().ToArray();
        if (snapshotIds.Length == 0)
            return new([], [], [], []);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.OwnerUserId == ownerUserId &&
                           (libraryScopeId == null || item.LibraryScopeId == libraryScopeId) &&
                           snapshotIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var ownedSnapshotIds = snapshots.Select(item => item.Id).ToArray();
        var identityIds = snapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var identities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           identityIds.Contains(item.Id) &&
                           (item.Verification == ProviderIdentityVerification.Verified ||
                            item.Verification == ProviderIdentityVerification.Pinned))
            .ToListAsync(cancellationToken);
        var overrides = await db.ManualTrackOverrides.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.OwnerUserId == ownerUserId &&
                           (libraryScopeId == null || item.LibraryScopeId == libraryScopeId) &&
                           item.RevokedAt == null &&
                           ownedSnapshotIds.Contains(item.ExternalSnapshotId))
            .ToListAsync(cancellationToken);
        var decisions = (await db.TrackMatches.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               item.OwnerUserId == ownerUserId &&
                               (libraryScopeId == null || item.LibraryScopeId == libraryScopeId) &&
                               ownedSnapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.DecisionVersion)
                .ThenByDescending(item => item.DecidedAt)
                .ToListAsync(cancellationToken))
            .GroupBy(item => item.ExternalSnapshotId)
            .Select(group => group.First())
            .ToArray();
        return new(snapshots, identities, overrides, decisions);
    }

    public async Task<int> EnsureSourceSnapshotsAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        CancellationToken cancellationToken = default)
    {
        var tracks = sourceTracks
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ProviderId) &&
                !string.IsNullOrWhiteSpace(item.ExternalId))
            .GroupBy(
                item => $"{item.ProviderId.Trim().ToLowerInvariant()}:{item.ExternalId.Trim()}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (tracks.Length == 0) return 0;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await db.Users.AsNoTracking()
            .Where(user => user.Status == PlatformUserStatus.Active)
            .GroupBy(user => user.TenantId)
            .Select(group => group.OrderBy(user => user.CreatedAt).First())
            .ToListAsync(cancellationToken);
        var created = 0;

        foreach (var owner in owners)
        {
            var backend = await db.BackendIdentities.AsNoTracking()
                .Where(identity => identity.TenantId == owner.TenantId &&
                                   identity.UserId == owner.Id)
                .OrderByDescending(identity => identity.LastSeenAt)
                .FirstOrDefaultAsync(cancellationToken);

            foreach (var providerGroup in tracks.GroupBy(
                         item => item.ProviderId.Trim().ToLowerInvariant(),
                         StringComparer.Ordinal))
            {
                var providerId = providerGroup.Key;
                var account = await db.ProviderAccounts.AsNoTracking()
                    .Where(item => item.Enabled && item.ProviderId == providerId &&
                                   (item.OwnerUserId == owner.Id ||
                                    item.TenantId == owner.TenantId && item.OwnerUserId == null ||
                                    item.TenantId == null))
                    .OrderByDescending(item => item.OwnerUserId == owner.Id)
                    .ThenByDescending(item => item.TenantId == owner.TenantId)
                    .ThenBy(item => item.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (account == null) continue;

                foreach (var track in providerGroup)
                {
                    var externalId = track.ExternalId.Trim();
                    var externalHash = Hash(externalId);
                    var identity = await db.ProviderTrackIdentities.SingleOrDefaultAsync(item =>
                        item.TenantId == owner.TenantId &&
                        item.ProviderId == providerId &&
                        item.ResourceKind == ProviderResourceKind.Track &&
                        item.CatalogNamespace == "default" &&
                        item.Scope == ProviderIdentityScope.Catalog &&
                        item.ExternalIdHash == externalHash,
                        cancellationToken);
                    var now = DateTimeOffset.UtcNow;
                    if (identity == null)
                    {
                        var canonical = new CanonicalRecordingRecord
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = owner.TenantId,
                            CreatedByUserId = owner.Id,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        identity = new ProviderTrackIdentityRecord
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = owner.TenantId,
                            CanonicalRecordingId = canonical.Id,
                            ProviderId = providerId,
                            ResourceKind = ProviderResourceKind.Track,
                            CatalogNamespace = "default",
                            Scope = ProviderIdentityScope.Catalog,
                            ExternalId = externalId,
                            ExternalIdHash = externalHash,
                            Verification = ProviderIdentityVerification.Verified,
                            VerificationMethod = "source-snapshot",
                            DecisionVersion = 1,
                            VerifiedAt = now,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        db.CanonicalRecordings.Add(canonical);
                        db.ProviderTrackIdentities.Add(identity);
                        created++;
                    }

                    var payloadJson = JsonSerializer.Serialize(new
                    {
                        providerId,
                        externalId,
                        track.Title,
                        track.Artist,
                        track.Album,
                        durationMs = track.DurationMilliseconds,
                        track.Isrc,
                        artworkReference = track.ArtworkReference
                    });
                    var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(item =>
                        item.TenantId == owner.TenantId &&
                        item.ProviderAccountId == account.Id &&
                        item.ResourceKind == "track" &&
                        item.ExternalIdHash == externalHash &&
                        item.SnapshotVersion == 1,
                        cancellationToken);
                    if (snapshot == null)
                    {
                        snapshot = new ExternalMetadataSnapshotRecord
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = owner.TenantId,
                            OwnerUserId = owner.Id,
                            ProviderAccountId = account.Id,
                            ProviderTrackIdentityId = identity.Id,
                            LibraryScopeId = account.LibraryScopeId ?? "music",
                            BackendInstanceId = backend?.BackendInstanceId ?? "source-import",
                            BackendPrincipalId = backend?.PrincipalId ?? owner.Id.ToString("N"),
                            Protocol = backend?.BackendType.ToLowerInvariant() ?? "jellyfin",
                            ProviderId = providerId,
                            ResourceKind = "track",
                            ExternalIdHash = externalHash,
                            SnapshotVersion = 1,
                            ProviderRevision = string.IsNullOrWhiteSpace(track.ProviderRevision)
                                ? "source-v1"
                                : track.ProviderRevision.Trim(),
                            PayloadJson = payloadJson,
                            PayloadSha256 = Hash(payloadJson),
                            CorrelationId = $"source-seed-{providerId}-{externalId}",
                            RetrievedAt = now
                        };
                        db.ExternalMetadataSnapshots.Add(snapshot);
                        db.TrackMatches.Add(new TrackMatchRecord
                        {
                            Id = Guid.CreateVersion7(),
                            TenantId = owner.TenantId,
                            OwnerUserId = owner.Id,
                            ExternalSnapshotId = snapshot.Id,
                            CanonicalRecordingId = identity.CanonicalRecordingId,
                            LibraryScopeId = snapshot.LibraryScopeId,
                            State = TrackMatchState.Unresolved,
                            Confidence = 0,
                            Threshold = 0.88,
                            DecisionVersion = 1,
                            PolicyVersion = "source-seed-v1",
                            CandidateResultsJson = "[]",
                            ReasonsJson = JsonSerializer.Serialize(new[]
                            {
                                "Source track is waiting for a playable match"
                            }),
                            WarningsJson = "[]",
                            CorrelationId = snapshot.CorrelationId,
                            DecidedAt = now
                        });
                        created++;
                    }
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    public async Task<DurableSpotifyMatchProjection> GetSpotifyProjectionAsync(
        string spotifyId,
        CancellationToken cancellationToken = default)
    {
        spotifyId = spotifyId.Trim();
        if (spotifyId.Length is < 3 or > 128)
            return new(null, false, []);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ProviderId == "spotify" && item.ExternalId == spotifyId)
            .ToListAsync(cancellationToken);
        if (identities.Count == 0)
            return new(null, false, []);

        var canonicalIds = identities.Select(item => item.CanonicalRecordingId).Distinct().ToArray();
        var providerIdentities = await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => canonicalIds.Contains(item.CanonicalRecordingId) &&
                               item.ProviderId != "spotify" &&
                               item.ResourceKind == ProviderResourceKind.Track)
                .OrderByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
                .ThenBy(item => item.ProviderId)
                .ThenByDescending(item => item.VerifiedAt)
                .ToListAsync(cancellationToken);
        var routes = providerIdentities
            .Where(item => ExternalTrackPlaybackPolicy.CanUseForPlayback(item.ProviderId))
            .DistinctBy(item => $"{item.ProviderId}:{item.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(item => new DurableProviderRoute(
                item.ProviderId,
                item.ExternalId,
                item.Verification == ProviderIdentityVerification.Pinned))
            .ToArray();

        var identityIds = identities.Select(item => item.Id).ToArray();
        var snapshotIds = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value))
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        var latestDecisions = snapshotIds.Length == 0
            ? []
            : (await db.TrackMatches.AsNoTracking()
                .Where(item => snapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.DecisionVersion)
                .ThenByDescending(item => item.DecidedAt)
                .ToListAsync(cancellationToken))
                .GroupBy(item => item.ExternalSnapshotId)
                .Select(group => group.First())
                .ToArray();
        var selectedLocalId = latestDecisions
            .Where(item => item.LibraryTrackId.HasValue &&
                           item.State is TrackMatchState.Accepted or TrackMatchState.Pinned)
            .OrderByDescending(item => item.DecidedAt)
            .Select(item => item.LibraryTrackId)
            .FirstOrDefault();
        var pinnedLocalId = snapshotIds.Length == 0
            ? null
            : await db.ManualTrackOverrides.AsNoTracking()
                .Where(item => snapshotIds.Contains(item.ExternalSnapshotId) &&
                               item.RevokedAt == null &&
                               item.Decision == ManualOverrideDecision.Pin &&
                               item.LibraryTrackId.HasValue)
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => item.LibraryTrackId)
                .FirstOrDefaultAsync(cancellationToken);
        var backendItemId = selectedLocalId.HasValue
            ? await db.LibraryTracks.AsNoTracking()
                .Where(item => item.Id == selectedLocalId.Value)
                .Select(item => item.BackendItemId)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var manualProviderDecision = latestDecisions.Any(item =>
            item.PolicyVersion.StartsWith("manual-provider-route-", StringComparison.Ordinal));
        return new(
            backendItemId,
            pinnedLocalId.HasValue && pinnedLocalId == selectedLocalId,
            manualProviderDecision
                ? routes.Select((route, index) => index == 0 ? route with { IsManual = true } : route).ToArray()
                : routes);
    }

    public async Task<int> PersistAutomatedSpotifyAsync(
        string spotifyId,
        PersistAutomatedTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        spotifyId = spotifyId.Trim();
        var targetType = command.TargetType.Trim().ToLowerInvariant();
        if (spotifyId.Length is < 3 or > 128 ||
            string.IsNullOrWhiteSpace(command.TargetId) ||
            targetType is not ("local" or "provider") ||
            command.Confidence is < 0 or > 1)
            return 0;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identityIds = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ProviderId == "spotify" && item.ExternalId == spotifyId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (identityIds.Length == 0) return 0;

        var snapshots = await db.ExternalMetadataSnapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value))
            .OrderByDescending(item => item.RetrievedAt)
            .ToListAsync(cancellationToken);
        var latestSnapshots = snapshots
            .GroupBy(item => new { item.TenantId, item.OwnerUserId, item.LibraryScopeId })
            .Select(group => group.First())
            .ToArray();
        var persisted = 0;

        foreach (var snapshot in latestSnapshots)
        {
            var sourceIdentity = await db.ProviderTrackIdentities.SingleAsync(
                item => item.Id == snapshot.ProviderTrackIdentityId!.Value,
                cancellationToken);
            var latest = await db.TrackMatches
                .Where(item => item.TenantId == snapshot.TenantId &&
                               item.ExternalSnapshotId == snapshot.Id)
                .OrderByDescending(item => item.DecisionVersion)
                .FirstOrDefaultAsync(cancellationToken);
            LibraryTrackRecord? localTrack = null;
            var state = TrackMatchState.Suggested;

            if (targetType == "local")
            {
                localTrack = await db.LibraryTracks
                    .Where(item =>
                        item.TenantId == snapshot.TenantId &&
                        item.OwnerUserId == snapshot.OwnerUserId &&
                        item.LibraryScopeId == snapshot.LibraryScopeId &&
                        item.BackendItemId == command.TargetId)
                    .OrderByDescending(item => item.UpdatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (localTrack == null) continue;
                if (localTrack.CanonicalRecordingId.HasValue &&
                    localTrack.CanonicalRecordingId != sourceIdentity.CanonicalRecordingId)
                    continue;
                if (!localTrack.CanonicalRecordingId.HasValue)
                {
                    localTrack.CanonicalRecordingId = sourceIdentity.CanonicalRecordingId;
                    localTrack.UpdatedAt = DateTimeOffset.UtcNow;
                }
                state = TrackMatchState.Accepted;
            }
            else
            {
                var provider = command.ExternalProvider?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(provider) ||
                    !ExternalTrackPlaybackPolicy.CanUseForPlayback(provider))
                    continue;
                var externalHash = Hash(command.TargetId);
                var providerIdentity = await db.ProviderTrackIdentities.SingleOrDefaultAsync(item =>
                    item.TenantId == snapshot.TenantId &&
                    item.ProviderId == provider &&
                    item.ResourceKind == ProviderResourceKind.Track &&
                    item.CatalogNamespace == "default" &&
                    item.Scope == ProviderIdentityScope.Catalog &&
                    item.ExternalIdHash == externalHash,
                    cancellationToken);
                if (providerIdentity != null &&
                    providerIdentity.CanonicalRecordingId != sourceIdentity.CanonicalRecordingId)
                    continue;
                if (providerIdentity == null)
                {
                    var now = DateTimeOffset.UtcNow;
                    db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = snapshot.TenantId,
                        CanonicalRecordingId = sourceIdentity.CanonicalRecordingId,
                        ProviderId = provider,
                        ResourceKind = ProviderResourceKind.Track,
                        CatalogNamespace = "default",
                        Scope = ProviderIdentityScope.Catalog,
                        ExternalId = command.TargetId,
                        ExternalIdHash = externalHash,
                        Verification = ProviderIdentityVerification.Verified,
                        VerificationMethod = "automatic-match",
                        DecisionVersion = (latest?.DecisionVersion ?? 0) + 1,
                        VerifiedAt = now,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
            }

            if (latest?.State == state &&
                latest.LibraryTrackId == localTrack?.Id &&
                latest.CanonicalRecordingId == sourceIdentity.CanonicalRecordingId)
                continue;

            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = snapshot.TenantId,
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                LibraryTrackId = localTrack?.Id,
                CanonicalRecordingId = sourceIdentity.CanonicalRecordingId,
                LibraryScopeId = snapshot.LibraryScopeId,
                State = state,
                Confidence = command.Confidence,
                Threshold = 0.88,
                DecisionVersion = (latest?.DecisionVersion ?? 0) + 1,
                PolicyVersion = "automatic-provider-neutral-v1",
                CandidateResultsJson = "[]",
                ReasonsJson = JsonSerializer.Serialize(new[]
                {
                    targetType == "local"
                        ? "Automatically matched indexed local track"
                        : $"Automatically matched {command.ExternalProvider} playback route"
                }),
                WarningsJson = "[]",
                CorrelationId = correlationId,
                DecidedAt = DateTimeOffset.UtcNow
            });
            persisted++;
        }

        if (persisted > 0)
            await db.SaveChangesAsync(cancellationToken);
        return persisted;
    }

    public async Task<TrackRematchCommandResult> RematchSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId && item.TenantId == actor.TenantId,
            cancellationToken);
        if (snapshot == null)
            return new(false, TrackMatchCommandFailure.NotFound, "Track snapshot was not found");
        if (!actor.IsAdministrator && snapshot.OwnerUserId != actor.UserId)
            return new(false, TrackMatchCommandFailure.Forbidden, "Track snapshot is outside your account");

        var activeOverrides = await db.ManualTrackOverrides
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ExternalSnapshotId == snapshot.Id &&
                           item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var activeOverride in activeOverrides)
        {
            activeOverride.RevokedAt = DateTimeOffset.UtcNow;
            activeOverride.Revision++;
        }

        var source = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == snapshot.ProviderTrackIdentityId.Value &&
                        item.TenantId == actor.TenantId,
                cancellationToken)
            : null;
        var candidates = await db.LibraryTracks.AsNoTracking()
            .Where(item =>
                item.TenantId == actor.TenantId &&
                item.OwnerUserId == snapshot.OwnerUserId &&
                item.LibraryScopeId == snapshot.LibraryScopeId)
            .ToListAsync(cancellationToken);
        var latestVersion = await db.TrackMatches
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ExternalSnapshotId == snapshot.Id)
            .Select(item => (int?)item.DecisionVersion)
            .MaxAsync(cancellationToken) ?? 0;
        var payload = ReadMetadata(snapshot.PayloadJson);
        var sourceTrack = new ExternalTrackMatchSnapshot(
            snapshot.Id.ToString("N"),
            source?.ProviderId ?? snapshot.ProviderId,
            source?.ExternalId ?? snapshot.ExternalIdHash,
            payload.Title ?? "Unknown",
            payload.Artist ?? "Unknown",
            payload.Album,
            null,
            ReadDurationSeconds(snapshot.PayloadJson),
            payload.Isrc,
            null,
            null);
        var localCandidates = candidates.Select(item => new LocalTrackMatchCandidate(
            item.Id,
            item.TenantId,
            item.OwnerUserId,
            item.BackendInstanceId,
            item.LibraryScopeId,
            item.BackendItemId,
            item.CanonicalRecordingId,
            item.Title,
            item.Artist,
            item.Album,
            item.AlbumArtist,
            item.DurationMilliseconds > 0
                ? (int?)Math.Round(item.DurationMilliseconds / 1000d)
                : null,
            item.Isrc,
            item.MusicBrainzRecordingId,
            null)).ToArray();
        var scope = new TrackMatchScope(
            actor.TenantId,
            snapshot.OwnerUserId,
            candidates.FirstOrDefault()?.BackendInstanceId ?? "unknown",
            snapshot.LibraryScopeId,
            snapshot.ProviderAccountId,
            2,
            snapshot.SnapshotVersion);
        var decision = decisionEngine.Decide(scope, sourceTrack, localCandidates);
        var selected = decision.SelectedLibraryTrackId.HasValue
            ? candidates.SingleOrDefault(item => item.Id == decision.SelectedLibraryTrackId.Value)
            : null;
        var state = Enum.Parse<TrackMatchState>(decision.State.ToString(), ignoreCase: true);
        db.TrackMatches.Add(new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = snapshot.OwnerUserId,
            ExternalSnapshotId = snapshot.Id,
            CanonicalRecordingId = selected?.CanonicalRecordingId,
            LibraryScopeId = snapshot.LibraryScopeId,
            LibraryTrackId = decision.SelectedLibraryTrackId,
            State = state,
            Confidence = decision.Confidence,
            Threshold = 0.88,
            DecisionVersion = latestVersion + 1,
            PolicyVersion = "manual-rematch-v2",
            CandidateResultsJson = JsonSerializer.Serialize(decision.Candidates),
            ReasonsJson = JsonSerializer.Serialize(decision.Reasons),
            WarningsJson = JsonSerializer.Serialize(decision.Warnings),
            CorrelationId = correlationId,
            DecidedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        return new(
            true,
            State: decision.State.ToString().ToLowerInvariant(),
            Confidence: decision.Confidence,
            CandidateCount: decision.Candidates.Count,
            DecisionVersion: latestVersion + 1);
    }

    public async Task<TrackMatchCommandResult> ClearSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        spotifyId = spotifyId.Trim();
        if (spotifyId.Length is < 3 or > 128)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "Spotify track id is invalid");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identityIds = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.ProviderId == "spotify" &&
                           item.ExternalId == spotifyId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (identityIds.Length == 0)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track is not indexed");

        var snapshots = db.ExternalMetadataSnapshots
            .Where(item => item.TenantId == actor.TenantId && item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value));
        if (!actor.IsAdministrator)
            snapshots = snapshots.Where(item => item.OwnerUserId == actor.UserId);
        var snapshot = await snapshots.OrderByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track has no match snapshot");

        var activeOverrides = await db.ManualTrackOverrides
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ExternalSnapshotId == snapshot.Id &&
                           item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var activeOverride in activeOverrides)
        {
            activeOverride.RevokedAt = now;
            activeOverride.Revision++;
        }

        var latest = await db.TrackMatches
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion)
            .FirstOrDefaultAsync(cancellationToken);
        db.TrackMatches.Add(new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            OwnerUserId = snapshot.OwnerUserId,
            ExternalSnapshotId = snapshot.Id,
            CanonicalRecordingId = latest?.CanonicalRecordingId,
            LibraryScopeId = snapshot.LibraryScopeId,
            State = TrackMatchState.Unresolved,
            Confidence = 0,
            Threshold = latest?.Threshold ?? 0.88,
            DecisionVersion = (latest?.DecisionVersion ?? 0) + 1,
            PolicyVersion = "manual-clear-v1",
            CandidateResultsJson = "[]",
            ReasonsJson = JsonSerializer.Serialize(new[] { "Manual match cleared" }),
            WarningsJson = JsonSerializer.Serialize(new[] { "Run rematch or select a new target" }),
            CorrelationId = correlationId,
            DecidedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return TrackMatchCommandResult.Success(snapshot.Id);
    }

    public async Task<TrackMatchCommandResult> ResolveSpotifyAsync(
        TrackMatchActor actor,
        string spotifyId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        spotifyId = spotifyId.Trim();
        if (spotifyId.Length is < 3 or > 128)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Invalid, "Spotify track id is invalid");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var identityIds = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.ProviderId == "spotify" &&
                           item.ExternalId == spotifyId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (identityIds.Length == 0)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track is not indexed");

        var snapshots = db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value));
        if (!actor.IsAdministrator)
            snapshots = snapshots.Where(item => item.OwnerUserId == actor.UserId);
        var snapshotId = await snapshots.OrderByDescending(item => item.RetrievedAt)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return snapshotId.HasValue
            ? await ResolveSnapshotAsync(actor, snapshotId.Value, command, correlationId, cancellationToken)
            : TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Spotify track has no match snapshot");
    }

    public async Task<TrackMatchCommandResult> ResolveSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        ResolveTrackMatchCommand command,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var targetType = command.TargetType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (targetType is not ("local" or "provider" or "reject"))
            return TrackMatchCommandResult.Fail(
                TrackMatchCommandFailure.Invalid,
                "TargetType must be local, provider, or reject");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId && item.TenantId == actor.TenantId,
            cancellationToken);
        if (snapshot == null)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Track snapshot was not found");
        if (!actor.IsAdministrator && snapshot.OwnerUserId != actor.UserId)
            return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Forbidden, "Track snapshot is outside your account");

        var sourceIdentity = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.SingleOrDefaultAsync(
                item => item.Id == snapshot.ProviderTrackIdentityId.Value && item.TenantId == actor.TenantId,
                cancellationToken)
            : null;
        var latestDecision = await db.TrackMatches
            .Where(item => item.TenantId == actor.TenantId && item.ExternalSnapshotId == externalSnapshotId)
            .OrderByDescending(item => item.DecisionVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var decisionVersion = (latestDecision?.DecisionVersion ?? 0) + 1;
        var now = DateTimeOffset.UtcNow;
        var activeOverride = await db.ManualTrackOverrides.SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId && item.ExternalSnapshotId == externalSnapshotId &&
            item.RevokedAt == null, cancellationToken);
        if (activeOverride != null)
        {
            activeOverride.RevokedAt = now;
            activeOverride.Revision++;
        }

        if (targetType == "local")
        {
            var localQuery = db.LibraryTracks.Where(item =>
                item.TenantId == actor.TenantId && item.LibraryScopeId == snapshot.LibraryScopeId);
            LibraryTrackRecord? localTrack;
            if (command.LibraryTrackId.HasValue)
                localTrack = await localQuery.SingleOrDefaultAsync(
                    item => item.Id == command.LibraryTrackId.Value, cancellationToken);
            else if (!string.IsNullOrWhiteSpace(command.BackendItemId))
                localTrack = await localQuery.OrderByDescending(item => item.UpdatedAt)
                    .FirstOrDefaultAsync(item => item.BackendItemId == command.BackendItemId, cancellationToken);
            else
                return TrackMatchCommandResult.Fail(
                    TrackMatchCommandFailure.Invalid,
                    "LibraryTrackId or BackendItemId is required for a local match");

            if (localTrack == null)
                return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.NotFound, "Local track was not found");
            if (!actor.IsAdministrator && localTrack.OwnerUserId != actor.UserId)
                return TrackMatchCommandResult.Fail(TrackMatchCommandFailure.Forbidden, "Local track is outside your account");

            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                LibraryTrackId = localTrack.Id,
                LibraryScopeId = snapshot.LibraryScopeId,
                Decision = ManualOverrideDecision.Pin,
                Reason = CleanReason(command.Reason, "Selected from the indexed local library"),
                DecisionVersion = decisionVersion,
                CreatedAt = now
            });
        }
        else if (targetType == "reject")
        {
            db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                LibraryScopeId = snapshot.LibraryScopeId,
                Decision = ManualOverrideDecision.Reject,
                Reason = CleanReason(command.Reason, "Rejected during manual review"),
                DecisionVersion = decisionVersion,
                CreatedAt = now
            });
        }
        else
        {
            var providerId = command.ExternalProvider?.Trim().ToLowerInvariant();
            var externalId = command.ExternalId?.Trim();
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(externalId))
                return TrackMatchCommandResult.Fail(
                    TrackMatchCommandFailure.Invalid,
                    "ExternalProvider and ExternalId are required for a provider match");
            if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(providerId))
                return TrackMatchCommandResult.Fail(
                    TrackMatchCommandFailure.Invalid,
                    "That provider cannot supply playback audio");

            var canonicalId = latestDecision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
            if (!canonicalId.HasValue)
                return TrackMatchCommandResult.Fail(
                    TrackMatchCommandFailure.Conflict,
                    "The source track has no canonical identity yet; rematch it first");

            var externalHash = Hash(externalId);
            var identity = await db.ProviderTrackIdentities.SingleOrDefaultAsync(item =>
                item.TenantId == actor.TenantId && item.ProviderId == providerId &&
                item.ResourceKind == ProviderResourceKind.Track && item.CatalogNamespace == "default" &&
                item.Scope == ProviderIdentityScope.Catalog && item.ExternalIdHash == externalHash,
                cancellationToken);
            if (identity != null && identity.CanonicalRecordingId != canonicalId.Value)
                return TrackMatchCommandResult.Fail(
                    TrackMatchCommandFailure.Conflict,
                    "That provider track is already linked to a different recording");
            if (identity == null)
            {
                db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = actor.TenantId,
                    CanonicalRecordingId = canonicalId.Value,
                    ProviderId = providerId,
                    ResourceKind = ProviderResourceKind.Track,
                    CatalogNamespace = "default",
                    Scope = ProviderIdentityScope.Catalog,
                    ExternalId = externalId,
                    ExternalIdHash = externalHash,
                    Verification = ProviderIdentityVerification.Pinned,
                    VerificationMethod = "manual-review",
                    DecisionVersion = decisionVersion,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            db.TrackMatches.Add(new TrackMatchRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = actor.TenantId,
                OwnerUserId = snapshot.OwnerUserId,
                ExternalSnapshotId = snapshot.Id,
                CanonicalRecordingId = canonicalId.Value,
                LibraryScopeId = snapshot.LibraryScopeId,
                State = TrackMatchState.Suggested,
                Confidence = 1,
                Threshold = 1,
                DecisionVersion = decisionVersion,
                PolicyVersion = "manual-provider-route-v1",
                CandidateResultsJson = "[]",
                ReasonsJson = JsonSerializer.Serialize(new[] { $"Manually selected {providerId} playback route" }),
                WarningsJson = "[]",
                CorrelationId = correlationId,
                DecidedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return TrackMatchCommandResult.Success(snapshot.Id);
    }

    private static string CleanReason(string? value, string fallback)
    {
        var reason = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return reason.Length <= 512 ? reason : reason[..512];
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool MatchesImmutableDecision(
        TrackMatchRecord record,
        MatchDecisionInput input) =>
        record.State == input.State &&
        record.LibraryTrackId == input.LibraryTrackId &&
        record.CanonicalRecordingId == input.CanonicalRecordingId &&
        record.PolicyVersion == input.PolicyVersion.Trim() &&
        record.Confidence == input.Confidence &&
        record.Threshold == input.Threshold &&
        record.CandidateResultsJson == input.CandidateResultsJson &&
        record.ReasonsJson == input.ReasonsJson &&
        record.WarningsJson == input.WarningsJson;

    private static async Task<ExternalMetadataSnapshotRecord> OwnedSnapshotAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
        Guid id,
        CancellationToken cancellationToken)
    {
        var record = await db.ExternalMetadataSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.TenantId == actor.TenantId,
                cancellationToken) ?? throw new KeyNotFoundException("Snapshot not found.");
        PersistenceGuard.RequireOwner(actor, record.OwnerUserId);
        return record;
    }

    private static void ValidateHash(string value, string name)
    {
        if (value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)) ||
            value != value.ToLowerInvariant())
            throw new ArgumentException("A normalized SHA-256 value is required.", name);
    }

    private static (string? Title, string? Artist, string? Album, string? Isrc) ReadMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return (
                ReadString(root, "title", "Title", "name", "Name"),
                ReadString(root, "artist", "Artist", "primaryArtist", "PrimaryArtist"),
                ReadString(root, "album", "Album"),
                ReadString(root, "isrc", "Isrc", "ISRC"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static int? ReadDurationSeconds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var name in new[] { "durationSeconds", "DurationSeconds", "duration", "Duration" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(value.GetDouble());
            }
            foreach (var name in new[] { "durationMs", "DurationMs", "durationMilliseconds", "DurationMilliseconds" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(value.GetDouble() / 1000d);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }
}
