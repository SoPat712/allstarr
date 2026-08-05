using System.Collections.Concurrent;
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
using allstarr.Models.Domain;
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

public sealed record AutomatedSourceMatchResult(
    string ProviderId,
    string ExternalId,
    TrackMatchReviewState State,
    string? LocalBackendItemId,
    string? Title,
    string? Artist,
    string? Album,
    long? DurationMilliseconds,
    string? Isrc,
    double Confidence);

public sealed record DurableProviderRoute(string ProviderId, string ExternalId, bool IsManual = false);

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
    long? DurationMilliseconds,
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
    bool SupportsExternalMatching => false;

    Task<int> EnsureSourceSnapshotsAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomatedSourceMatchResult>> MatchSourceTracksAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        string correlationId,
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
        Guid? externalSnapshotId = null,
        int scanLimit = 5000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryTrackRecord>> SearchLocalTracksAsync(
        TrackMatchActor actor,
        string query,
        string? libraryScopeId = null,
        int limit = 20,
        ExternalTrackMatchSnapshot? source = null,
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

    Task<IReadOnlyList<TrackMatchRecord>> RecordDecisionsAsync(
        ProtocolExecutionContext context,
        IReadOnlyCollection<MatchDecisionInput> inputs,
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

    Task<TrackRematchCommandResult> RematchSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<TrackRematchCommandResult> RematchSnapshotAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        string correlationId,
        string policyVersion,
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
    IPlatformClock clock,
    PlaylistPlayableSearchService? playableSearch = null) : ITrackMatchRepository
{
    private const int ConcurrentWriteRetries = 3;
    private readonly ConcurrentDictionary<
        (Guid TenantId, Guid UserId, bool IsAdministrator, Guid SnapshotId),
        Lazy<Task<TrackRematchCommandResult>>> _rematches = [];

    public bool SupportsExternalMatching => playableSearch != null;

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
        CancellationToken cancellationToken = default) =>
        (await RecordDecisionsAsync(context, [input], cancellationToken)).Single();

    public async Task<IReadOnlyList<TrackMatchRecord>> RecordDecisionsAsync(
        ProtocolExecutionContext context,
        IReadOnlyCollection<MatchDecisionInput> inputs,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        var requested = inputs.ToArray();
        if (requested.Length == 0)
            return [];
        foreach (var input in requested)
            ValidateDecisionInput(input);
        if (requested.Select(item => (item.ExternalSnapshotId, item.DecisionVersion)).Distinct().Count() !=
            requested.Length)
            throw new ArgumentException("A match decision version may appear only once.", nameof(inputs));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshotIds = requested.Select(item => item.ExternalSnapshotId).Distinct().ToArray();
        var snapshots = await db.ExternalMetadataSnapshots
            .Where(item => item.TenantId == actor.TenantId && snapshotIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (snapshots.Count != snapshotIds.Length)
            throw new UnauthorizedAccessException("A source snapshot is outside the actor scope.");
        foreach (var snapshot in snapshots.Values)
        {
            PersistenceGuard.RequireOwner(actor, snapshot.OwnerUserId);
            PersistenceGuard.RequireLibrary(context, snapshot.LibraryScopeId);
        }

        var libraryTrackIds = requested
            .Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Distinct()
            .ToArray();
        var libraryTracks = await db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && libraryTrackIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        foreach (var input in requested.Where(item => item.LibraryTrackId.HasValue))
        {
            var snapshot = snapshots[input.ExternalSnapshotId];
            if (!libraryTracks.TryGetValue(input.LibraryTrackId!.Value, out var libraryTrack) ||
                libraryTrack.OwnerUserId != snapshot.OwnerUserId ||
                libraryTrack.LibraryScopeId != snapshot.LibraryScopeId)
                throw new UnauthorizedAccessException(
                    "The selected library track is outside the snapshot scope.");
        }

        var versions = requested.Select(item => item.DecisionVersion).Distinct().ToArray();
        var existing = await db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           snapshotIds.Contains(item.ExternalSnapshotId) &&
                           versions.Contains(item.DecisionVersion))
            .ToDictionaryAsync(
                item => (item.ExternalSnapshotId, item.DecisionVersion),
                cancellationToken);
        var now = clock.UtcNow;
        var records = new List<TrackMatchRecord>(requested.Length);
        foreach (var input in requested)
        {
            if (existing.TryGetValue((input.ExternalSnapshotId, input.DecisionVersion), out var stored))
            {
                if (!MatchesImmutableDecision(stored, input))
                    throw new InvalidOperationException(
                        "The match decision version already exists with different content.");
                records.Add(stored);
                continue;
            }

            var snapshot = snapshots[input.ExternalSnapshotId];
            var record = ToRecord(
                input, actor.TenantId, snapshot.OwnerUserId, snapshot.LibraryScopeId,
                context.CorrelationId, now);
            db.TrackMatches.Add(record);
            records.Add(record);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return records;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winners = await db.TrackMatches.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               snapshotIds.Contains(item.ExternalSnapshotId) &&
                               versions.Contains(item.DecisionVersion))
                .ToDictionaryAsync(
                    item => (item.ExternalSnapshotId, item.DecisionVersion),
                    cancellationToken);
            foreach (var input in requested)
            {
                if (!winners.TryGetValue(
                        (input.ExternalSnapshotId, input.DecisionVersion), out var winner))
                    throw;
                if (!MatchesImmutableDecision(winner, input))
                    throw new InvalidOperationException(
                        "A concurrent match decision used the same version with different content.");
            }
            return requested
                .Select(input => winners[(input.ExternalSnapshotId, input.DecisionVersion)])
                .ToArray();
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
        var latestDecision = await db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var overrideTrackId = input.Decision == ManualOverrideDecision.Pin
            ? input.LibraryTrackId
            : TrackMatchOverridePolicy.TopCandidateLibraryTrackId(
                  latestDecision?.CandidateResultsJson) ?? latestDecision?.LibraryTrackId;
        if (overrideTrackId.HasValue &&
            !await db.LibraryTracks.AnyAsync(item =>
                    item.Id == overrideTrackId &&
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
                active.LibraryTrackId == overrideTrackId &&
                active.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion &&
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
            LibraryTrackId = overrideTrackId,
            LibraryScopeId = input.LibraryScopeId,
            Decision = input.Decision,
            Reason = input.Reason.Trim(),
            DecisionVersion = version + 1,
            MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
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
        Guid? externalSnapshotId = null,
        int scanLimit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshotsQuery = db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId);
        if (!actor.IsAdministrator)
            snapshotsQuery = snapshotsQuery.Where(item => item.OwnerUserId == actor.UserId);
        if (externalSnapshotId.HasValue)
            snapshotsQuery = snapshotsQuery.Where(item => item.Id == externalSnapshotId.Value);
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
        var sourceIdentityIds = snapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var sourceIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           sourceIdentityIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var canonicalIds = decisions.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)
            .Concat(sourceIdentities.Select(item => item.CanonicalRecordingId))
            .Distinct()
            .ToArray();
        var libraryQuery = db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           (libraryIds.Contains(item.Id) ||
                            item.CanonicalRecordingId.HasValue &&
                            canonicalIds.Contains(item.CanonicalRecordingId.Value)));
        if (!actor.IsAdministrator)
            libraryQuery = libraryQuery.Where(item => item.OwnerUserId == actor.UserId);
        if (!string.IsNullOrWhiteSpace(libraryScopeId))
            libraryQuery = libraryQuery.Where(item => item.LibraryScopeId == libraryScopeId.Trim());
        var library = await libraryQuery.ToListAsync(cancellationToken);
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
        ExternalTrackMatchSnapshot? source = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 50);
        var patterns = query.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%")
            .ToArray();
        var tracks = db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId);
        if (!actor.IsAdministrator)
            tracks = tracks.Where(item => item.OwnerUserId == actor.UserId);
        if (!string.IsNullOrWhiteSpace(libraryScopeId))
            tracks = tracks.Where(item => item.LibraryScopeId == libraryScopeId.Trim());
        IReadOnlyList<LibraryTrackRecord> indexed = source == null
            ? []
            : await tracks.ToListAsync(cancellationToken);
        HashSet<Guid> automatic = source == null
            ? []
            : decisionEngine.PrepareCandidates(indexed.Select(ToLocalCandidate))
                .Select(source)
                .Select(item => item.LibraryTrackId)
                .ToHashSet();
        foreach (var pattern in patterns)
            tracks = tracks.Where(item =>
                EF.Functions.ILike(item.Title, pattern, "\\") ||
                EF.Functions.ILike(item.Artist, pattern, "\\") ||
                item.Album != null && EF.Functions.ILike(item.Album, pattern, "\\"));
        var searched = await tracks
            .OrderBy(item => item.Artist)
            .ThenBy(item => item.Title)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (automatic.Count == 0) return searched;
        var indexedById = indexed.ToDictionary(item => item.Id);
        var selected = decisionEngine.ScoreCandidates(
                source!,
                indexed.Where(item => automatic.Contains(item.Id)).Select(ToLocalCandidate))
            .Select(item => indexedById[item.LibraryTrackId]);
        return selected.Concat(searched).DistinctBy(item => item.Id).Take(limit).ToArray();
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
        var sourceIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId &&
                           identityIds.Contains(item.Id) &&
                           (item.Verification == ProviderIdentityVerification.Verified ||
                            item.Verification == ProviderIdentityVerification.Pinned))
            .ToListAsync(cancellationToken);
        var canonicalIds = sourceIdentities
            .Select(item => item.CanonicalRecordingId)
            .Distinct()
            .ToArray();
        var identities = canonicalIds.Length == 0
            ? sourceIdentities
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               canonicalIds.Contains(item.CanonicalRecordingId) &&
                               item.ResourceKind == ProviderResourceKind.Track &&
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
        var decisions = await LatestDecisions(db.TrackMatches.AsNoTracking()
                .Where(item => item.TenantId == actor.TenantId &&
                               item.OwnerUserId == ownerUserId &&
                               (libraryScopeId == null || item.LibraryScopeId == libraryScopeId) &&
                               ownedSnapshotIds.Contains(item.ExternalSnapshotId)))
            .ToArrayAsync(cancellationToken);
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
                        durationMilliseconds = track.DurationMilliseconds is > 0
                            ? track.DurationMilliseconds
                            : null,
                        durationProvenance = track.DurationMilliseconds is > 0 ? providerId : null,
                        durationRetrievedAt = track.DurationMilliseconds is > 0 ? now : (DateTimeOffset?)null,
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
                            SourceSnapshotVersion = snapshot.SnapshotVersion,
                            MatcherVersion = "unscored",
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

    public async Task<IReadOnlyList<AutomatedSourceMatchResult>> MatchSourceTracksAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var tracks = sourceTracks
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ProviderId) &&
                !string.IsNullOrWhiteSpace(item.ExternalId))
            .GroupBy(
                item => SourceKey(item.ProviderId, item.ExternalId),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        if (tracks.Count == 0) return [];

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var providerIds = tracks.Values
            .Select(item => item.ProviderId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var externalIds = tracks.Values
            .Select(item => item.ExternalId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var identities = (await db.ProviderTrackIdentities
                .Where(item =>
                    providerIds.Contains(item.ProviderId) &&
                    externalIds.Contains(item.ExternalId) &&
                    item.ResourceKind == ProviderResourceKind.Track)
                .ToListAsync(cancellationToken))
            .Where(item => tracks.ContainsKey(SourceKey(item.ProviderId, item.ExternalId)))
            .ToArray();
        if (identities.Length == 0) return [];

        var identityById = identities.ToDictionary(item => item.Id);
        var identityIds = identityById.Keys.ToArray();
        var snapshots = (await db.ExternalMetadataSnapshots
                .Where(item =>
                    item.ProviderTrackIdentityId.HasValue &&
                    identityIds.Contains(item.ProviderTrackIdentityId.Value))
                .OrderByDescending(item => item.RetrievedAt)
                .ToListAsync(cancellationToken))
            .GroupBy(item => new
            {
                item.ProviderTrackIdentityId,
                item.TenantId,
                item.OwnerUserId,
                item.LibraryScopeId
            })
            .Select(group => group.First())
            .ToArray();
        if (snapshots.Length == 0) return [];

        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var activeOverrides = await db.ManualTrackOverrides.AsNoTracking()
            .Where(item =>
                snapshotIds.Contains(item.ExternalSnapshotId) &&
                item.RevokedAt == null)
            .ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
        var tenantIds = snapshots.Select(item => item.TenantId).Distinct().ToArray();
        var ownerIds = snapshots.Select(item => item.OwnerUserId).Distinct().ToArray();
        var libraryScopes = snapshots.Select(item => item.LibraryScopeId).Distinct().ToArray();
        var libraryTracks = await db.LibraryTracks
            .Where(item =>
                tenantIds.Contains(item.TenantId) &&
                ownerIds.Contains(item.OwnerUserId) &&
                libraryScopes.Contains(item.LibraryScopeId))
            .ToListAsync(cancellationToken);
        var latestDecisions = await LatestDecisions(db.TrackMatches
                .Where(item => snapshotIds.Contains(item.ExternalSnapshotId)))
            .ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
        var scopedLibraries = libraryTracks
            .GroupBy(item => new
            {
                item.TenantId,
                item.OwnerUserId,
                item.LibraryScopeId,
                item.BackendInstanceId
            })
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var tracks = group.ToArray();
                    return (
                        Tracks: tracks,
                        ById: tracks.ToDictionary(item => item.Id),
                        PlayableIds: tracks.Select(item => item.Id).ToHashSet(),
                        Candidates: decisionEngine.PrepareCandidates(tracks.Select(ToLocalCandidate)));
                });
        var emptyLibrary = (
            Tracks: Array.Empty<LibraryTrackRecord>(),
            ById: new Dictionary<Guid, LibraryTrackRecord>(),
            PlayableIds: new HashSet<Guid>(),
            Candidates: decisionEngine.PrepareCandidates([]));

        var results = new List<AutomatedSourceMatchResult>(snapshots.Length);
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = identityById[snapshot.ProviderTrackIdentityId!.Value];
            var seed = tracks[SourceKey(identity.ProviderId, identity.ExternalId)];
            latestDecisions.TryGetValue(snapshot.Id, out var latest);
            activeOverrides.TryGetValue(snapshot.Id, out var manual);
            if (!scopedLibraries.TryGetValue(new
            {
                snapshot.TenantId,
                snapshot.OwnerUserId,
                snapshot.LibraryScopeId,
                snapshot.BackendInstanceId
            }, out var library))
                library = emptyLibrary;
            if (manual?.Decision == ManualOverrideDecision.Pin ||
                manual?.Decision == ManualOverrideDecision.Reject && !manual.LibraryTrackId.HasValue)
            {
                var classification = TrackClassifier.Classify(
                    manual,
                    latest,
                    playableLibraryTrackIds: library.PlayableIds);
                var protectedLocal = classification.LibraryTrackId is { } protectedId
                    ? library.ById.GetValueOrDefault(protectedId)
                    : null;
                results.Add(ToAutomatedResult(
                    seed,
                    Enum.Parse<TrackMatchReviewState>(classification.State.ToString(), true),
                    protectedLocal,
                    latest?.Confidence ?? 0));
                continue;
            }

            var candidates = library.Candidates;
            var libraryIndexRevision = candidates.Revision;
            var scope = new TrackMatchScope(
                snapshot.TenantId,
                snapshot.OwnerUserId,
                snapshot.BackendInstanceId,
                snapshot.LibraryScopeId,
                snapshot.ProviderAccountId,
                2,
                snapshot.SnapshotVersion);
            var source = new ExternalTrackMatchSnapshot(
                snapshot.Id.ToString("N"),
                identity.ProviderId,
                identity.ExternalId,
                seed.Title,
                seed.Artist,
                seed.Album,
                null,
                seed.DurationMilliseconds is > 0 ? seed.DurationMilliseconds : null,
                seed.Isrc,
                null,
                null);
            var rejectedOverride =
                manual?.Decision == ManualOverrideDecision.Reject &&
                manual.LibraryTrackId.HasValue &&
                manual.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion
                    ? new ScopedTrackMatchOverride(
                        snapshot.TenantId,
                        snapshot.OwnerUserId,
                        snapshot.LibraryScopeId,
                        source.ProviderId,
                        source.ExternalId,
                        null,
                        new HashSet<Guid> { manual.LibraryTrackId.Value })
                    : null;
            var decision = decisionEngine.Decide(
                scope, source, candidates, rejectedOverride);
            var selected = decision.SelectedLibraryTrackId is { } selectedId
                ? library.ById[selectedId]
                : null;
            if (selected != null && !selected.CanonicalRecordingId.HasValue)
            {
                selected.CanonicalRecordingId = identity.CanonicalRecordingId;
                selected.UpdatedAt = now;
            }

            var state = Enum.Parse<TrackMatchState>(decision.State.ToString(), true);
            var unchanged = latest?.State == state &&
                            latest.LibraryTrackId == selected?.Id &&
                            Math.Abs(latest.Confidence - decision.Confidence) < 0.0001 &&
                            latest.SourceSnapshotVersion == snapshot.SnapshotVersion &&
                            latest.LibraryIndexRevision == libraryIndexRevision &&
                            latest.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion &&
                            latest.PolicyVersion == "automatic-provider-neutral-v2";
            if (!unchanged)
            {
                var input = MatchDecisionInput.FromDecision(
                    snapshot.Id,
                    identity.CanonicalRecordingId,
                    decision,
                    (latest?.DecisionVersion ?? 0) + 1,
                    snapshot.SnapshotVersion,
                    libraryIndexRevision,
                    "automatic-provider-neutral-v2");
                db.TrackMatches.Add(ToRecord(
                    input, snapshot.TenantId, snapshot.OwnerUserId, snapshot.LibraryScopeId,
                    correlationId, now));
            }

            results.Add(ToAutomatedResult(seed, decision.State, selected, decision.Confidence));
        }

        await db.SaveChangesAsync(cancellationToken);
        return results;
    }

    private static string SourceKey(string providerId, string externalId) =>
        $"{providerId.Trim().ToLowerInvariant()}:{externalId.Trim()}";

    private static IQueryable<TrackMatchRecord> LatestDecisions(
        IQueryable<TrackMatchRecord> decisions)
    {
        var versions = decisions
            .GroupBy(item => item.ExternalSnapshotId)
            .Select(group => new
            {
                ExternalSnapshotId = group.Key,
                DecisionVersion = group.Max(item => item.DecisionVersion)
            });
        return from decision in decisions
               join version in versions
                   on new { decision.ExternalSnapshotId, decision.DecisionVersion }
                   equals new { version.ExternalSnapshotId, version.DecisionVersion }
               select decision;
    }

    private static LocalTrackMatchCandidate ToLocalCandidate(LibraryTrackRecord item) => new(
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
        item.DurationMilliseconds is > 0 ? item.DurationMilliseconds : null,
        item.Isrc,
        item.MusicBrainzRecordingId,
        null,
        ReadProviderTrackIds(item.ProviderIdsJson));

    private static AutomatedSourceMatchResult ToAutomatedResult(
        SourceTrackSeed source,
        TrackMatchReviewState state,
        LibraryTrackRecord? local,
        double confidence) => new(
        source.ProviderId.Trim().ToLowerInvariant(),
        source.ExternalId.Trim(),
        state,
        local?.BackendItemId,
        local?.Title,
        local?.Artist,
        local?.Album,
        local?.DurationMilliseconds is > 0 ? local.DurationMilliseconds : null,
        local?.Isrc,
        confidence);

    public async Task<TrackRematchCommandResult> RematchSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        string correlationId,
        CancellationToken cancellationToken = default)
        => await CoalesceRematchAsync(
            actor,
            externalSnapshotId,
            () => RematchSnapshotAsync(
                actor, externalSnapshotId, correlationId, "manual-rematch-v3", null,
                ConcurrentWriteRetries, cancellationToken),
            cancellationToken);

    public async Task<TrackRematchCommandResult> RematchSnapshotAsync(
        ProtocolExecutionContext context,
        Guid externalSnapshotId,
        string correlationId,
        string policyVersion,
        CancellationToken cancellationToken = default)
    {
        var actor = context.RequireActor();
        var matchActor = new TrackMatchActor(
                actor.TenantId,
                actor.EffectiveUserId ?? throw new UnauthorizedAccessException("A user owner is required."),
                actor.Kind == ProviderActorKind.Administrator);
        return await CoalesceRematchAsync(
            matchActor,
            externalSnapshotId,
            () => RematchSnapshotAsync(
                matchActor,
                externalSnapshotId,
                correlationId,
                policyVersion,
                context,
                ConcurrentWriteRetries,
                cancellationToken),
            cancellationToken);
    }

    private async Task<TrackRematchCommandResult> CoalesceRematchAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        Func<Task<TrackRematchCommandResult>> rematch,
        CancellationToken cancellationToken)
    {
        var key = (actor.TenantId, actor.UserId, actor.IsAdministrator, externalSnapshotId);
        var created = new Lazy<Task<TrackRematchCommandResult>>(
            rematch,
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = _rematches.GetOrAdd(key, created);
        try
        {
            return await pending.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            _rematches.TryRemove(
                new KeyValuePair<
                    (Guid TenantId, Guid UserId, bool IsAdministrator, Guid SnapshotId),
                    Lazy<Task<TrackRematchCommandResult>>>(key, pending));
        }
    }

    private async Task<TrackRematchCommandResult> RematchSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        string correlationId,
        string policyVersion,
        ProtocolExecutionContext? execution,
        int retriesRemaining,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId && item.TenantId == actor.TenantId,
            cancellationToken);
        if (snapshot == null)
            return new(false, TrackMatchCommandFailure.NotFound, "Track snapshot was not found");
        if (!actor.IsAdministrator && snapshot.OwnerUserId != actor.UserId)
            return new(false, TrackMatchCommandFailure.Forbidden, "Track snapshot is outside your account");

        var source = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.SingleOrDefaultAsync(
                item => item.Id == snapshot.ProviderTrackIdentityId.Value &&
                        item.TenantId == actor.TenantId,
                cancellationToken)
            : null;
        source ??= await db.ProviderTrackIdentities
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ProviderId == snapshot.ProviderId &&
                           item.ResourceKind == ProviderResourceKind.Track &&
                           item.ExternalIdHash == snapshot.ExternalIdHash &&
                           (item.Scope == ProviderIdentityScope.Catalog ||
                            item.ProviderAccountId == snapshot.ProviderAccountId))
            .OrderByDescending(item => item.ProviderAccountId == snapshot.ProviderAccountId)
            .FirstOrDefaultAsync(cancellationToken);
        var candidates = await db.LibraryTracks.AsNoTracking()
            .Where(item =>
                item.TenantId == actor.TenantId &&
                item.OwnerUserId == snapshot.OwnerUserId &&
                item.LibraryScopeId == snapshot.LibraryScopeId &&
                item.BackendInstanceId == snapshot.BackendInstanceId)
            .ToListAsync(cancellationToken);
        var manual = await db.ManualTrackOverrides.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == actor.TenantId &&
            item.ExternalSnapshotId == snapshot.Id &&
            item.RevokedAt == null,
            cancellationToken);
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
            payload.AlbumArtist,
            ReadDurationMilliseconds(snapshot.PayloadJson),
            payload.Isrc,
            null,
            null);
        var localCandidates = decisionEngine.PrepareCandidates(candidates.Select(ToLocalCandidate));
        var libraryIndexRevision = localCandidates.Revision;
        var scope = new TrackMatchScope(
            actor.TenantId,
            snapshot.OwnerUserId,
            snapshot.BackendInstanceId,
            snapshot.LibraryScopeId,
            snapshot.ProviderAccountId,
            2,
            snapshot.SnapshotVersion);
        var rejectedOverride =
            manual?.Decision == ManualOverrideDecision.Reject &&
            manual.LibraryTrackId.HasValue &&
            manual.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion
                ? new ScopedTrackMatchOverride(
                    snapshot.TenantId,
                    snapshot.OwnerUserId,
                    snapshot.LibraryScopeId,
                    sourceTrack.ProviderId,
                    sourceTrack.ExternalId,
                    null,
                    new HashSet<Guid> { manual.LibraryTrackId.Value })
                : null;
        var decision = decisionEngine.Decide(scope, sourceTrack, localCandidates, rejectedOverride);
        PlayableTrackMatch? playable = null;
        if (execution != null &&
            playableSearch != null &&
            manual?.Decision is not ManualOverrideDecision.Pin &&
            !(manual?.Decision == ManualOverrideDecision.Reject && !manual.LibraryTrackId.HasValue) &&
            decision.State is not (TrackMatchReviewState.Accepted or TrackMatchReviewState.Pinned))
        {
            var cachedRoutes = source == null
                ? []
                : await db.ProviderTrackIdentities.AsNoTracking()
                    .Where(item =>
                        item.TenantId == source.TenantId &&
                        item.CanonicalRecordingId == source.CanonicalRecordingId &&
                        item.Id != source.Id &&
                        item.ResourceKind == ProviderResourceKind.Track &&
                        item.VerificationMethod != "source-snapshot-hash" &&
                        (item.Verification == ProviderIdentityVerification.Verified ||
                         item.Verification == ProviderIdentityVerification.Pinned))
                    .ToArrayAsync(cancellationToken);
            playable = source == null
                ? null
                : await playableSearch.ReuseAsync(
                    execution, sourceTrack, scope, cachedRoutes, cancellationToken);
            playable ??= await playableSearch.MatchAsync(
                execution,
                sourceTrack,
                scope,
                candidates.Select(ToLocalCandidate).ToArray(),
                rejectedOverride,
                cancellationToken);
            decision = playable.Decision;
        }
        var selected = decision.SelectedLibraryTrackId.HasValue
            ? candidates.SingleOrDefault(item => item.Id == decision.SelectedLibraryTrackId.Value)
            : null;
        var externalRoutable =
            (decision.State is TrackMatchReviewState.Accepted or TrackMatchReviewState.Suggested) &&
            playable != null &&
            selected == null;
        var selectedExternal = externalRoutable ? playable!.SelectedExternal : null;
        Guid? canonicalRecordingId = selected?.CanonicalRecordingId ?? source?.CanonicalRecordingId;
        if (externalRoutable && selectedExternal != null)
        {
            if (source == null)
            {
                var canonical = new CanonicalRecordingRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = actor.TenantId,
                    CreatedByUserId = actor.UserId,
                    Isrc = payload.Isrc,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                };
                db.CanonicalRecordings.Add(canonical);
                source = AddSourceSnapshotIdentity(
                    db, snapshot, canonical.Id, latestVersion + 1, clock.UtcNow);
            }
            canonicalRecordingId = await LinkExternalIdentitiesAsync(
                db,
                source,
                selectedExternal,
                playable!.RoutableExternalCandidates,
                decision.State,
                latestVersion + 1,
                clock.UtcNow,
                cancellationToken);
        }
        var input = externalRoutable && canonicalRecordingId.HasValue
            ? MatchDecisionInput.FromExternalDecision(
                snapshot.Id,
                canonicalRecordingId.Value,
                decision,
                latestVersion + 1,
                snapshot.SnapshotVersion,
                libraryIndexRevision,
                policyVersion)
            : MatchDecisionInput.FromDecision(
                snapshot.Id,
                canonicalRecordingId,
                decision,
                latestVersion + 1,
                snapshot.SnapshotVersion,
                libraryIndexRevision,
                policyVersion);
        var record = ToRecord(
            input, actor.TenantId, snapshot.OwnerUserId, snapshot.LibraryScopeId,
            correlationId, clock.UtcNow);
        db.TrackMatches.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            var winner = await db.TrackMatches.AsNoTracking().SingleOrDefaultAsync(item =>
                item.TenantId == actor.TenantId &&
                item.OwnerUserId == snapshot.OwnerUserId &&
                item.LibraryScopeId == snapshot.LibraryScopeId &&
                item.ExternalSnapshotId == snapshot.Id &&
                item.DecisionVersion == input.DecisionVersion,
                cancellationToken);
            var comparable = winner?.CanonicalRecordingId.HasValue == true &&
                             input.CanonicalRecordingId.HasValue &&
                             !input.LibraryTrackId.HasValue
                ? input with { CanonicalRecordingId = winner.CanonicalRecordingId }
                : input;
            if (winner != null && MatchesImmutableDecision(winner, comparable))
            {
                record = winner;
            }
            else
            {
                if (retriesRemaining <= 0 || !IsConcurrentMatchWrite(exception))
                    throw;
                return await RematchSnapshotAsync(
                    actor,
                    externalSnapshotId,
                    correlationId,
                    policyVersion,
                    execution,
                    retriesRemaining - 1,
                    cancellationToken);
            }
        }

        return new(
            true,
            State: decision.State.ToString().ToLowerInvariant(),
            Confidence: decision.Confidence,
            CandidateCount: decision.Candidates.Count,
            DecisionVersion: record.DecisionVersion);
    }

    private static async Task<Guid> LinkExternalIdentitiesAsync(
        AllstarrDbContext db,
        ProviderTrackIdentityRecord source,
        Song selected,
        IReadOnlyList<Song> routable,
        TrackMatchReviewState state,
        int decisionVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var verificationMethod = state == TrackMatchReviewState.Suggested
            ? "automatic-suggestion"
            : "automatic-match";
        var canonicalRecordingId = await LinkExternalIdentityAsync(
            db, source, selected, source.CanonicalRecordingId, true, verificationMethod,
            decisionVersion, now, cancellationToken);
        foreach (var alternate in routable.Where(song =>
                     !string.Equals(song.ExternalProvider, selected.ExternalProvider, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(song.ExternalId, selected.ExternalId, StringComparison.Ordinal)))
        {
            await LinkExternalIdentityAsync(
                db, source, alternate, canonicalRecordingId, false, verificationMethod,
                decisionVersion, now, cancellationToken);
        }
        return canonicalRecordingId;
    }

    private static async Task<Guid> LinkExternalIdentityAsync(
        AllstarrDbContext db,
        ProviderTrackIdentityRecord source,
        Song song,
        Guid canonicalRecordingId,
        bool primary,
        string verificationMethod,
        int decisionVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var providerId = song.ExternalProvider!.Trim().ToLowerInvariant() switch
        {
            "applemusic" => "apple-download",
            var value => value
        };
        var externalId = song.ExternalId!.Trim();
        var externalHash = Hash(externalId);
        var identity = await db.ProviderTrackIdentities.SingleOrDefaultAsync(item =>
            item.TenantId == source.TenantId &&
            item.ProviderId == providerId &&
            item.ResourceKind == ProviderResourceKind.Track &&
            item.CatalogNamespace == "default" &&
            item.Scope == ProviderIdentityScope.Catalog &&
            item.ExternalIdHash == externalHash,
            cancellationToken);
        if (identity != null)
        {
            if (primary)
                canonicalRecordingId = identity.CanonicalRecordingId;
            if (source.CanonicalRecordingId != canonicalRecordingId)
            {
                source.CanonicalRecordingId = canonicalRecordingId;
                source.UpdatedAt = now;
            }
            if (verificationMethod == "automatic-match" &&
                identity.VerificationMethod == "automatic-suggestion")
            {
                identity.VerificationMethod = verificationMethod;
                identity.DecisionVersion = decisionVersion;
                identity.VerifiedAt = now;
                identity.UpdatedAt = now;
            }
            return canonicalRecordingId;
        }

        db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = source.TenantId,
            CanonicalRecordingId = canonicalRecordingId,
            ProviderId = providerId,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Catalog,
            ExternalId = externalId,
            ExternalIdHash = externalHash,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = verificationMethod,
            DecisionVersion = decisionVersion,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        return canonicalRecordingId;
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
            SourceSnapshotVersion = snapshot.SnapshotVersion,
            MatcherVersion = "manual",
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
        sourceIdentity ??= await db.ProviderTrackIdentities
            .Where(item => item.TenantId == actor.TenantId &&
                           item.ProviderId == snapshot.ProviderId &&
                           item.ResourceKind == ProviderResourceKind.Track &&
                           item.ExternalIdHash == snapshot.ExternalIdHash &&
                           (item.Scope == ProviderIdentityScope.Catalog ||
                            item.ProviderAccountId == snapshot.ProviderAccountId))
            .OrderByDescending(item => item.ProviderAccountId == snapshot.ProviderAccountId)
            .FirstOrDefaultAsync(cancellationToken);
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
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
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
                LibraryTrackId = TrackMatchOverridePolicy.TopCandidateLibraryTrackId(
                    latestDecision?.CandidateResultsJson) ?? latestDecision?.LibraryTrackId,
                LibraryScopeId = snapshot.LibraryScopeId,
                Decision = ManualOverrideDecision.Reject,
                Reason = CleanReason(command.Reason, "Rejected during manual review"),
                DecisionVersion = decisionVersion,
                MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion,
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
            if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(providerId) ||
                playableSearch?.CanUseProvider(providerId) == false)
                return TrackMatchCommandResult.Fail(
                    TrackMatchCommandFailure.Invalid,
                    "That provider cannot supply playback audio");

            var canonicalId = latestDecision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
            if (!canonicalId.HasValue)
            {
                var metadata = ReadMetadata(snapshot.PayloadJson);
                var canonical = new CanonicalRecordingRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = actor.TenantId,
                    CreatedByUserId = actor.UserId,
                    Isrc = metadata.Isrc,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.CanonicalRecordings.Add(canonical);
                canonicalId = canonical.Id;
            }

            if (sourceIdentity == null)
            {
                sourceIdentity = AddSourceSnapshotIdentity(
                    db, snapshot, canonicalId.Value, decisionVersion, now);
            }

            var externalHash = Hash(externalId);
            var identity =
                sourceIdentity.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                sourceIdentity.ExternalIdHash == externalHash
                    ? sourceIdentity
                    : await db.ProviderTrackIdentities.SingleOrDefaultAsync(item =>
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
            else
            {
                identity.ExternalId = externalId;
                identity.Verification = ProviderIdentityVerification.Pinned;
                identity.VerificationMethod = "manual-review";
                identity.DecisionVersion = decisionVersion;
                identity.VerifiedAt = now;
                identity.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return TrackMatchCommandResult.Success(snapshot.Id);
    }

    private static ProviderTrackIdentityRecord AddSourceSnapshotIdentity(
        AllstarrDbContext db,
        ExternalMetadataSnapshotRecord snapshot,
        Guid canonicalRecordingId,
        int decisionVersion,
        DateTimeOffset now)
    {
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = snapshot.TenantId,
            CanonicalRecordingId = canonicalRecordingId,
            ProviderAccountId = snapshot.ProviderAccountId,
            ProviderId = snapshot.ProviderId,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Account,
            ExternalId = snapshot.ExternalIdHash,
            ExternalIdHash = snapshot.ExternalIdHash,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "source-snapshot-hash",
            DecisionVersion = decisionVersion,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProviderTrackIdentities.Add(identity);
        return identity;
    }

    private static string CleanReason(string? value, string fallback)
    {
        var reason = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return reason.Length <= 512 ? reason : reason[..512];
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateDecisionInput(MatchDecisionInput input)
    {
        if (input.DecisionVersion <= 0 ||
            input.SourceSnapshotVersion <= 0 ||
            string.IsNullOrWhiteSpace(input.MatcherVersion) ||
            input.Confidence is < 0 or > 1 ||
            input.Threshold is < 0 or > 1 ||
            string.IsNullOrWhiteSpace(input.PolicyVersion))
            throw new ArgumentException("The match decision is incomplete.", nameof(input));
        PersistenceGuard.ValidateSafeJson(input.CandidateResultsJson, nameof(input.CandidateResultsJson));
        PersistenceGuard.ValidateSafeJson(input.ReasonsJson, nameof(input.ReasonsJson));
        PersistenceGuard.ValidateSafeJson(input.WarningsJson, nameof(input.WarningsJson));
        if (input.State is TrackMatchState.Accepted or TrackMatchState.Suggested &&
                !input.LibraryTrackId.HasValue &&
                !input.CanonicalRecordingId.HasValue ||
            input.State == TrackMatchState.Pinned &&
                !input.LibraryTrackId.HasValue ||
            input.State is TrackMatchState.Unresolved or TrackMatchState.Rejected or
                TrackMatchState.Ambiguous &&
                input.LibraryTrackId.HasValue)
            throw new ArgumentException(
                "The selected library track does not match the decision state.",
                nameof(input));
        if (input.State == TrackMatchState.Accepted && input.Confidence < input.Threshold)
            throw new ArgumentException(
                "A match below its acceptance threshold cannot be accepted for automatic action.",
                nameof(input));
    }

    private static bool MatchesImmutableDecision(
        TrackMatchRecord record,
        MatchDecisionInput input) =>
        record.State == input.State &&
        record.LibraryTrackId == input.LibraryTrackId &&
        record.CanonicalRecordingId == input.CanonicalRecordingId &&
        record.SourceSnapshotVersion == input.SourceSnapshotVersion &&
        record.LibraryIndexRevision == input.LibraryIndexRevision &&
        record.MatcherVersion == input.MatcherVersion.Trim() &&
        record.PolicyVersion == input.PolicyVersion.Trim() &&
        record.Confidence == input.Confidence &&
        record.Threshold == input.Threshold &&
        record.CandidateResultsJson == input.CandidateResultsJson &&
        record.ReasonsJson == input.ReasonsJson &&
        record.WarningsJson == input.WarningsJson;

    private static bool IsConcurrentMatchWrite(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException
                {
                    SqlState: Npgsql.PostgresErrorCodes.DeadlockDetected
                })
                return true;
            if (current is not Npgsql.PostgresException
                {
                    SqlState: Npgsql.PostgresErrorCodes.UniqueViolation
                } postgres)
                continue;
            if (postgres.ConstraintName is
                "IX_provider_track_identity_account_exact" or
                "IX_provider_track_identity_catalog_exact" or
                "IX_track_match_scoped_decision")
                return true;
        }
        return false;
    }

    private static TrackMatchRecord ToRecord(
        MatchDecisionInput input,
        Guid tenantId,
        Guid ownerUserId,
        string libraryScopeId,
        string correlationId,
        DateTimeOffset decidedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            ExternalSnapshotId = input.ExternalSnapshotId,
            LibraryTrackId = input.LibraryTrackId,
            CanonicalRecordingId = input.CanonicalRecordingId,
            LibraryScopeId = libraryScopeId,
            State = input.State,
            Confidence = input.Confidence,
            Threshold = input.Threshold,
            DecisionVersion = input.DecisionVersion,
            SourceSnapshotVersion = input.SourceSnapshotVersion,
            LibraryIndexRevision = input.LibraryIndexRevision,
            MatcherVersion = input.MatcherVersion.Trim(),
            PolicyVersion = input.PolicyVersion.Trim(),
            CandidateResultsJson = input.CandidateResultsJson,
            ReasonsJson = input.ReasonsJson,
            WarningsJson = input.WarningsJson,
            CorrelationId = correlationId,
            DecidedAt = decidedAt
        };

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

    private static (string? Title, string? Artist, string? Album, string? AlbumArtist, string? Isrc) ReadMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var artists = root.TryGetProperty("Artists", out var artistValues) &&
                          artistValues.ValueKind == JsonValueKind.Array
                ? artistValues.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray()
                : [];
            return (
                ReadString(root, "title", "Title", "name", "Name"),
                artists.Length > 0
                    ? string.Join(", ", artists)
                    : ReadString(root, "artist", "Artist", "primaryArtist", "PrimaryArtist"),
                ReadString(root, "album", "Album"),
                ReadString(root, "albumArtist", "AlbumArtist"),
                ReadString(root, "isrc", "Isrc", "ISRC"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static long? ReadDurationMilliseconds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var name in new[] { "durationMilliseconds", "DurationMilliseconds", "durationMs", "DurationMs" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                    return (long)Math.Round(value.GetDouble());
            }
            foreach (var name in new[] { "durationSeconds", "DurationSeconds", "duration", "Duration" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                    return (long)Math.Round(value.GetDouble() * 1000d);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadProviderTrackIds(string json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (providerId, trackId) in values ?? [])
            {
                if (!string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(trackId))
                {
                    normalized[providerId.Trim()] = trackId.Trim();
                }
            }
            return normalized;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
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
