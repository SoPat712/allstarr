using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record PlaylistOrchestrationRequest(
    Guid PlaylistLinkId,
    long Generation,
    Guid? SourceSnapshotId = null,
    Guid? JobId = null,
    Guid? ScheduleId = null);

public sealed record PlaylistOrchestrationResult(
    PlaylistMaterializationPlan Plan,
    Guid? RunId,
    PlaylistSyncState? State,
    bool BackendWriteAttempted,
    bool ReusedRun,
    string? ErrorCode = null);

public sealed record PlaylistRefreshResult(Guid SnapshotId, int SnapshotVersion, string SourceRevision);

public interface IPlaylistOrchestrationService
{
    Task<PlaylistOrchestrationResult> RunAsync(ProtocolExecutionContext execution,
        PlaylistOrchestrationRequest request, CancellationToken cancellationToken = default);
    Task<PlaylistRefreshResult> RefreshAsync(ProtocolExecutionContext execution, Guid playlistLinkId,
        Guid? jobId = null, CancellationToken cancellationToken = default);
}

public interface IProviderPlaylistSourceGateway
{
    Task<CollectedPlaylistSourceSnapshot> CollectAsync(
        ProtocolExecutionContext context,
        PlaylistLinkRecord link,
        CancellationToken cancellationToken);

    Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProtocolExecutionContext context,
        PlaylistLinkRecord link,
        ProviderPlaylistArtworkRequest request,
        CancellationToken cancellationToken) => Task.FromResult(
            ProviderOutcome<ProviderPlaylistArtwork>.Failure(
                new ProviderError(ProviderErrorKind.CapabilityUnavailable)));
}

public sealed class ProviderPlaylistSourceGateway(
    IProviderRegistry registry,
    ProviderPlaylistSnapshotCollector collector,
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IPlatformClock clock) : IProviderPlaylistSourceGateway
{
    public async Task<CollectedPlaylistSourceSnapshot> CollectAsync(
        ProtocolExecutionContext context,
        PlaylistLinkRecord link,
        CancellationToken cancellationToken)
    {
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == link.ProviderAccountId && item.ProviderId == link.SourceProviderId && item.Enabled,
            cancellationToken) ?? throw new UnauthorizedAccessException("The selected playlist account is unavailable.");
        if (account.Scope == ProviderAccountScope.User &&
            (account.TenantId != actor.TenantId || account.OwnerUserId != link.OwnerUserId) ||
            account.Scope == ProviderAccountScope.Library &&
            (account.TenantId != actor.TenantId || account.LibraryScopeId != link.LibraryScopeId))
            throw new UnauthorizedAccessException("The playlist account is outside the link scope.");

        var capability = registry.GetRequiredCapability<IProviderPlaylistCapability>(
            link.SourceProviderId, ProviderCapabilityKind.Playlist);
        var accountContext = new ProviderAccountContext(
            account.Id, account.ProviderId, account.Scope, account.Revision, account.Enabled,
            account.TenantId, account.OwnerUserId, account.LibraryScopeId,
            "playlist-link", account.SecretReferenceId);
        var providerContext = new ProviderExecutionContext(
            actor, account.ProviderId, accountContext,
            new ProviderLibraryContext(actor.TenantId, link.LibraryScopeId),
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, false, account.Scope == ProviderAccountScope.Global, false,
                [account.ProviderId]),
            "playlist-snapshot", context.CorrelationId, clock.UtcNow.AddMinutes(5), cancellationToken);
        var result = await collector.CollectAsync(
            capability, providerContext,
            new ProviderPlaylistSnapshotRequest(new ProviderExternalResourceId(
                account.ProviderId, ProviderResourceKind.Playlist, link.SourcePlaylistId)));
        if (!result.IsSuccess || result.Snapshot == null)
            throw new PlaylistSourceUnavailableException(result.Error?.Kind.ToString() ?? "source_unavailable");
        return result.Snapshot;
    }

    public async Task<ProviderOutcome<ProviderPlaylistArtwork>> ResolveArtworkAsync(
        ProtocolExecutionContext context, PlaylistLinkRecord link,
        ProviderPlaylistArtworkRequest request, CancellationToken cancellationToken)
    {
        var actor = context.RequireActor();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == link.ProviderAccountId && item.ProviderId == link.SourceProviderId && item.Enabled,
            cancellationToken);
        if (account == null) return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.AccountNeedsConfiguration));
        if (account.Scope == ProviderAccountScope.User &&
            (account.TenantId != actor.TenantId || account.OwnerUserId != link.OwnerUserId) ||
            account.Scope == ProviderAccountScope.Library &&
            (account.TenantId != actor.TenantId || account.LibraryScopeId != link.LibraryScopeId))
            return ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.Forbidden));
        var capability = registry.GetRequiredCapability<IProviderPlaylistCapability>(
            link.SourceProviderId, ProviderCapabilityKind.Playlist);
        var accountContext = new ProviderAccountContext(account.Id, account.ProviderId, account.Scope,
            account.Revision, account.Enabled, account.TenantId, account.OwnerUserId, account.LibraryScopeId,
            "playlist-link-artwork", account.SecretReferenceId);
        var providerContext = new ProviderExecutionContext(actor, account.ProviderId, accountContext,
            new ProviderLibraryContext(actor.TenantId, link.LibraryScopeId),
            new ProviderExecutionPolicy(new ProviderQualityPolicy(ProviderAudioQuality.Any,
                    ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow, false,
                account.Scope == ProviderAccountScope.Global, false, [account.ProviderId]),
            "playlist-artwork", context.CorrelationId, clock.UtcNow.AddMinutes(2), cancellationToken);
        return await capability.ResolveArtworkAsync(providerContext, request);
    }
}

public sealed class PlaylistSourceUnavailableException(string code) : Exception("The playlist source is unavailable.")
{
    public string Code { get; } = code;
}

public interface IBackendPlaylistTargetResolver
{
    IBackendPlaylistTarget Resolve(string targetProtocol);
}

public sealed class BackendPlaylistTargetResolver(IEnumerable<IBackendPlaylistTarget> targets) : IBackendPlaylistTargetResolver
{
    private readonly IReadOnlyDictionary<BackendPlaylistFamily, IBackendPlaylistTarget> _targets = targets
        .ToDictionary(item => item.Family);

    public IBackendPlaylistTarget Resolve(string targetProtocol)
    {
        var family = targetProtocol.Trim().ToLowerInvariant() switch
        {
            "jellyfin" => BackendPlaylistFamily.Jellyfin,
            "subsonic" or "opensubsonic" or "navidrome" => BackendPlaylistFamily.Subsonic,
            _ => throw new NotSupportedException("The playlist target protocol is unsupported.")
        };
        return _targets.TryGetValue(family, out var target)
            ? target
            : throw new InvalidOperationException("The playlist target is not registered.");
    }
}

public sealed class PlaylistOrchestrationService : IPlaylistOrchestrationService
{
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly IProviderPlaylistSourceGateway _source;
    private readonly IBackendPlaylistTargetResolver _targets;
    private readonly PlaylistMaterializationPlanner _planner;
    private readonly TrackMatchDecisionEngine _matcher;
    private readonly ITrackMatchRepository _trackMatches;
    private readonly IPlatformClock _clock;

    public PlaylistOrchestrationService(
        IDbContextFactory<AllstarrDbContext> factory,
        IProviderPlaylistSourceGateway source,
        IBackendPlaylistTargetResolver targets,
        PlaylistMaterializationPlanner planner,
        TrackMatchDecisionEngine matcher,
        ITrackMatchRepository trackMatches,
        IPlatformClock clock) =>
        (_factory, _source, _targets, _planner, _matcher, _trackMatches, _clock) =
        (factory, source, targets, planner, matcher, trackMatches, clock);

    public async Task<PlaylistOrchestrationResult> RunAsync(
        ProtocolExecutionContext execution,
        PlaylistOrchestrationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Generation <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        var actor = execution.RequireActor();
        await using var initial = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await initial.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == request.PlaylistLinkId && item.TenantId == actor.TenantId,
            cancellationToken) ?? throw new KeyNotFoundException("Playlist link not found.");
        PersistenceGuard.RequireOwner(actor, link.OwnerUserId);
        PersistenceGuard.RequireLibrary(execution, link.LibraryScopeId);
        if (!link.Enabled) throw new InvalidOperationException("The playlist is paused. Resume it before synchronizing.");

        var snapshot = request.SourceSnapshotId.HasValue
            ? await LoadSnapshotAsync(initial, link, request.SourceSnapshotId.Value, cancellationToken)
            : await CollectAndPersistAsync(execution, link, request.JobId, cancellationToken);
        var (source, decisions, decisionIds) = await MatchAndLoadAsync(
            execution, link, snapshot, cancellationToken);
        var mode = link.Mode == PlaylistLinkMode.Virtual
            ? PlaylistPlanMode.Virtual
            : link.MaterializationMode == PlaylistMaterializationMode.Recreate
                ? PlaylistPlanMode.Recreate
                : PlaylistPlanMode.Reconcile;
        var owned = await initial.PlaylistTargetMemberships.AsNoTracking()
            .Where(item => item.TenantId == actor.TenantId && item.PlaylistLinkId == link.Id && item.Active)
            .Select(item => item.TargetEntryId).ToListAsync(cancellationToken);
        var rules = new PlaylistPlanningRules(
            link.RuleVersion, request.Generation, link.PreserveManualEntries, link.MirrorStaleEntries,
            owned, link.SyncName, link.SyncDescription, link.SyncArtwork);
        var planningTarget = new PlaylistPlanningTarget(
            link.TargetProtocol, link.TargetBackendInstanceId, link.TargetPlaylistId);
        var plan = _planner.Plan(mode, source, decisions, planningTarget, rules);
        if (!plan.RequiresBackendWrite)
            return new(plan, null, null, false, false);

        await using (var duplicateDb = await _factory.CreateDbContextAsync(cancellationToken))
        {
            var duplicate = await duplicateDb.PlaylistSyncRuns.AsNoTracking().SingleOrDefaultAsync(item =>
                item.TenantId == actor.TenantId && item.PlaylistLinkId == link.Id &&
                item.IdempotencyKey == plan.IdempotencyKey, cancellationToken);
            if (duplicate != null)
                return new(plan, duplicate.Id, duplicate.State, false, true, duplicate.ConflictCode);
        }

        var target = _targets.Resolve(link.TargetProtocol);
        ProviderPlaylistArtwork? resolvedArtwork = null;
        string? artworkIssue = null;
        if (link.SyncArtwork && snapshot.ArtworkReferenceKey != null)
        {
            if (!target.Capabilities.CanWriteArtwork)
            {
                artworkIssue = "artwork_target_unsupported";
            }
            else
            {
                var artwork = await _source.ResolveArtworkAsync(execution, link,
                    new ProviderPlaylistArtworkRequest(new ProviderArtworkReference(
                        new ProviderExternalResourceId(link.SourceProviderId, ProviderResourceKind.Playlist,
                            link.SourcePlaylistId), revision: snapshot.ProviderRevision)), cancellationToken);
                if (artwork.IsSuccess) resolvedArtwork = artwork.RequireValue();
                else artworkIssue = $"artwork_{artwork.Error!.Kind.ToString().ToLowerInvariant()}";
            }
        }
        var targetContext = new BackendPlaylistTargetContext(
            link.TargetBackendInstanceId,
            execution.VerifiedBackendPrincipalId,
            link.TargetCredentialReferenceId?.ToString(),
            link.TenantId);
        BackendPlaylistSnapshot? before = null;
        if (link.TargetPlaylistId != null)
        {
            var read = await target.ReadAsync(targetContext, link.TargetPlaylistId, cancellationToken);
            if (read.IsSuccess) before = read.Value;
            else if (read.Status is not BackendPlaylistTargetStatus.NotFound)
                return await RecordFailureAsync(execution, request, link, snapshot, plan, decisionIds,
                    read.Status == BackendPlaylistTargetStatus.Conflict ? PlaylistSyncState.Conflicted : PlaylistSyncState.Failed,
                    read.ErrorCode ?? read.Status.ToString(), before?.Fingerprint, backendWriteAttempted: false,
                    cancellationToken);
        }
        else
        {
            var found = await target.FindByNameAsync(targetContext, snapshot.Name, cancellationToken);
            if (found.IsSuccess) before = found.Value;
            else if (found.Status is not BackendPlaylistTargetStatus.NotFound)
                return await RecordFailureAsync(execution, request, link, snapshot, plan, decisionIds,
                    found.Status == BackendPlaylistTargetStatus.Conflict ? PlaylistSyncState.Conflicted : PlaylistSyncState.Failed,
                    found.ErrorCode ?? found.Status.ToString(), null, backendWriteAttempted: false,
                    cancellationToken);
        }

        var write = await target.WriteAsync(targetContext, new BackendPlaylistWriteRequest(
            plan.Mode == PlaylistPlanMode.Recreate ? BackendPlaylistWriteMode.Recreate : BackendPlaylistWriteMode.Reconcile,
            new BackendPlaylistMetadata(plan.Metadata.Name ?? snapshot.Name,
                plan.Metadata.Description, resolvedArtwork?.Bytes, resolvedArtwork?.ContentType),
            plan.OrderedBackendItemIds, plan.IdempotencyKey,
            before?.BackendPlaylistId ?? link.TargetPlaylistId,
            before?.NativeRevision, before?.Fingerprint,
            owned, link.MirrorStaleEntries), cancellationToken);
        if (!write.IsSuccess || write.Value == null)
            return await RecordFailureAsync(execution, request, link, snapshot, plan, decisionIds,
                write.Status == BackendPlaylistTargetStatus.Conflict ? PlaylistSyncState.Conflicted : PlaylistSyncState.Failed,
                write.ErrorCode ?? write.Status.ToString(), before?.Fingerprint, backendWriteAttempted: true,
                cancellationToken);

        var receipt = artworkIssue == null
            ? write.Value
            : write.Value with
            {
                UnsupportedMetadataFields = write.Value.UnsupportedMetadataFields
                    .Concat([artworkIssue]).Distinct(StringComparer.Ordinal).ToArray()
            };

        return await PersistSuccessAsync(
            execution, request, link, snapshot, plan, decisionIds, before, receipt, cancellationToken);
    }

    public async Task<PlaylistRefreshResult> RefreshAsync(
        ProtocolExecutionContext execution,
        Guid playlistLinkId,
        Guid? jobId = null,
        CancellationToken cancellationToken = default)
    {
        var actor = execution.RequireActor();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == playlistLinkId && item.TenantId == actor.TenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Playlist link not found.");
        PersistenceGuard.RequireOwner(actor, link.OwnerUserId);
        PersistenceGuard.RequireLibrary(execution, link.LibraryScopeId);
        if (!link.Enabled) throw new InvalidOperationException("The playlist is paused. Resume it before refreshing.");
        var snapshot = await CollectAndPersistAsync(execution, link, jobId, cancellationToken);
        _ = await MatchAndLoadAsync(execution, link, snapshot, cancellationToken);
        return new PlaylistRefreshResult(snapshot.Id, snapshot.SnapshotVersion, snapshot.ProviderRevision);
    }

    private async Task<PlaylistSourceSnapshotRecord> CollectAndPersistAsync(
        ProtocolExecutionContext execution, PlaylistLinkRecord link, Guid? jobId, CancellationToken cancellationToken)
    {
        var collected = await _source.CollectAsync(execution, link, cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PlaylistSourceSnapshots.AsNoTracking().Where(item =>
            item.TenantId == link.TenantId && item.PlaylistLinkId == link.Id &&
            item.ProviderRevision == collected.SourceRevision)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        if (existing != null) return existing;
        var version = (await db.PlaylistSourceSnapshots.Where(item => item.TenantId == link.TenantId && item.PlaylistLinkId == link.Id)
            .MaxAsync(item => (int?)item.SnapshotVersion, cancellationToken) ?? 0) + 1;
        var now = _clock.UtcNow;
        var externalByTrack = new Dictionary<string, ExternalMetadataSnapshotRecord>(StringComparer.Ordinal);
        var externalBySourceEntry = new Dictionary<string, ExternalMetadataSnapshotRecord>(StringComparer.Ordinal);
        var distinctEntries = collected.Entries
            .OrderBy(item => item.SourcePosition)
            .GroupBy(item => item.ProviderTrackIdHash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var payloads = distinctEntries.ToDictionary(
            entry => entry.ProviderTrackIdHash,
            entry =>
            {
                var payload = JsonSerializer.Serialize(new
                {
                    entry.ProviderTrackIdHash,
                    entry.Title,
                    entry.Artists,
                    entry.Album,
                    durationSeconds = entry.Duration?.TotalSeconds,
                    entry.Isrc,
                    entry.IsExplicit,
                    entry.CanonicalRecordingId
                });
                return (Payload: payload, PayloadHash: Hash(payload));
            },
            StringComparer.Ordinal);
        var storedExternals = new List<ExternalMetadataSnapshotRecord>();
        foreach (var hashes in payloads.Keys.Chunk(500))
        {
            storedExternals.AddRange(await db.ExternalMetadataSnapshots.AsNoTracking()
                .Where(item => item.TenantId == link.TenantId &&
                               item.ProviderAccountId == link.ProviderAccountId &&
                               item.ResourceKind == "track" &&
                               hashes.Contains(item.ExternalIdHash))
                .ToListAsync(cancellationToken));
        }
        var exactExternals = storedExternals
            .Where(item => item.ProviderRevision == collected.SourceRevision)
            .GroupBy(item => (item.ExternalIdHash, item.ProviderRevision, item.PayloadSha256))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.SnapshotVersion).First());
        var latestVersions = storedExternals
            .GroupBy(item => item.ExternalIdHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.SnapshotVersion), StringComparer.Ordinal);
        foreach (var entry in collected.Entries.OrderBy(item => item.SourcePosition))
        {
            if (externalByTrack.TryGetValue(entry.ProviderTrackIdHash, out var duplicateExternal))
            {
                externalBySourceEntry[entry.SourceEntryIdHash] = duplicateExternal;
                continue;
            }
            var (payload, payloadHash) = payloads[entry.ProviderTrackIdHash];
            exactExternals.TryGetValue((entry.ProviderTrackIdHash, collected.SourceRevision, payloadHash), out var external);
            if (external == null)
            {
                var externalVersion = latestVersions.GetValueOrDefault(entry.ProviderTrackIdHash) + 1;
                external = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = link.TenantId,
                    OwnerUserId = link.OwnerUserId,
                    ProviderAccountId = link.ProviderAccountId,
                    SourceJobId = jobId,
                    LibraryScopeId = link.LibraryScopeId,
                    BackendInstanceId = link.TargetBackendInstanceId,
                    BackendPrincipalId = execution.VerifiedBackendPrincipalId,
                    Protocol = link.TargetProtocol,
                    ProviderId = link.SourceProviderId,
                    ResourceKind = "track",
                    ExternalIdHash = entry.ProviderTrackIdHash,
                    SnapshotVersion = externalVersion,
                    ProviderRevision = collected.SourceRevision,
                    PayloadJson = payload,
                    PayloadSha256 = payloadHash,
                    CorrelationId = execution.CorrelationId,
                    RetrievedAt = now
                };
                db.ExternalMetadataSnapshots.Add(external);
            }
            externalByTrack[entry.ProviderTrackIdHash] = external;
            externalBySourceEntry[entry.SourceEntryIdHash] = external;
        }
        var playlistPayload = JsonSerializer.Serialize(new
        {
            collected.SourceRevision,
            collected.Name,
            collected.Description,
            collected.ArtworkReferenceKey,
            entries = collected.Entries.Select(item => new { item.SourcePosition, item.SourceEntryIdHash, item.ProviderTrackIdHash })
        });
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = link.TenantId,
            OwnerUserId = link.OwnerUserId,
            PlaylistLinkId = link.Id,
            ProviderAccountId = link.ProviderAccountId,
            SourceJobId = jobId,
            SnapshotVersion = version,
            ProviderRevision = collected.SourceRevision,
            ETag = collected.SourceETag,
            Name = collected.Name,
            Description = collected.Description,
            ArtworkReferenceKey = collected.ArtworkReferenceKey,
            PayloadSha256 = Hash(playlistPayload),
            CorrelationId = execution.CorrelationId,
            RetrievedAt = now
        };
        db.PlaylistSourceSnapshots.Add(snapshot);
        db.PlaylistSourceEntries.AddRange(collected.Entries.OrderBy(item => item.SourcePosition).Select(entry =>
            new PlaylistSourceEntryRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = link.TenantId,
                PlaylistSourceSnapshotId = snapshot.Id,
                ExternalMetadataSnapshotId = externalBySourceEntry[entry.SourceEntryIdHash].Id,
                SourcePosition = entry.SourcePosition,
                SourceEntryIdHash = entry.SourceEntryIdHash
            }));
        await db.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<PlaylistSourceSnapshotRecord> LoadSnapshotAsync(
        AllstarrDbContext db, PlaylistLinkRecord link, Guid id, CancellationToken cancellationToken) =>
        await db.PlaylistSourceSnapshots.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == id && item.TenantId == link.TenantId && item.PlaylistLinkId == link.Id &&
            item.OwnerUserId == link.OwnerUserId && item.ProviderAccountId == link.ProviderAccountId,
            cancellationToken) ?? throw new UnauthorizedAccessException("The source snapshot is outside the playlist link.");

    private async Task<(ImmutablePlaylistSourceSnapshot Source, IReadOnlyList<PersistedPlaylistMatchDecision> Decisions, IReadOnlyDictionary<Guid, Guid?> DecisionIds)> MatchAndLoadAsync(
        ProtocolExecutionContext execution,
        PlaylistLinkRecord link,
        PlaylistSourceSnapshotRecord snapshot,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var entries = await db.PlaylistSourceEntries.AsNoTracking().Where(item =>
            item.TenantId == link.TenantId && item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderBy(item => item.SourcePosition).ToListAsync(cancellationToken);
        var externalIds = entries.Select(entry => entry.ExternalMetadataSnapshotId).ToList();

        var externals = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
            externalIds.Contains(item.Id) && item.TenantId == link.TenantId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var candidates = await db.LibraryTracks.AsNoTracking().Where(item =>
            item.TenantId == link.TenantId && item.OwnerUserId == link.OwnerUserId &&
            item.LibraryScopeId == link.LibraryScopeId && item.BackendInstanceId == link.TargetBackendInstanceId)
            .ToListAsync(cancellationToken);

        // Optimization: Map candidates once outside the loop instead of doing it N times
        var mappedCandidates = candidates.Select(ToCandidate).ToArray();
        var candidateIndex = new TrackMatchCandidateIndex(mappedCandidates);
        var libraryIndexRevision = TrackMatchDecisionEngine.LibraryIndexRevision(mappedCandidates);
        var candidateById = candidates.ToDictionary(item => item.Id);

        var actor = execution.RequireActor();
        var resolution = await _trackMatches.GetResolutionDataAsync(
            new TrackMatchActor(
                actor.TenantId,
                actor.EffectiveUserId ?? link.OwnerUserId,
                actor.Kind == ProviderActorKind.Administrator),
            link.OwnerUserId,
            link.LibraryScopeId,
            externalIds,
            cancellationToken);
        var storedByExternalId = resolution.LatestDecisions
            .ToDictionary(item => item.ExternalSnapshotId);
        var allManualOverrides = resolution.ActiveOverrides
            .ToDictionary(item => item.ExternalSnapshotId);

        var decisions = new List<PersistedPlaylistMatchDecision>(entries.Count);
        var decisionIds = new Dictionary<Guid, Guid?>(entries.Count);

        foreach (var entry in entries)
        {
            var external = externals[entry.ExternalMetadataSnapshotId];

            storedByExternalId.TryGetValue(external.Id, out var stored);
            if (stored == null ||
                stored.SourceSnapshotVersion != external.SnapshotVersion ||
                stored.LibraryIndexRevision != libraryIndexRevision ||
                stored.MatcherVersion != TrackMatchDecisionEngine.AlgorithmVersion ||
                stored.PolicyVersion != link.PolicyVersion)
            {
                using var payload = JsonDocument.Parse(external.PayloadJson);
                var root = payload.RootElement;
                var artists = root.GetProperty("Artists").EnumerateArray().Select(item => item.GetString()).Where(item => item != null).ToArray();
                var source = new ExternalTrackMatchSnapshot(external.Id.ToString("N"), link.SourceProviderId,
                    external.ExternalIdHash, root.TryGetProperty("Title", out var title) ? title.GetString() ?? "Unknown" : "Unknown",
                    artists.Length > 0 ? string.Join(", ", artists) : "Unknown",
                    root.TryGetProperty("Album", out var album) ? album.GetString() : null, null,
                    root.TryGetProperty("durationSeconds", out var duration) && duration.ValueKind == JsonValueKind.Number ? (int?)Math.Round(duration.GetDouble()) : null,
                    root.TryGetProperty("Isrc", out var isrc) ? isrc.GetString() : null, null,
                    root.TryGetProperty("IsExplicit", out var explicitValue) && explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? explicitValue.GetBoolean() : null);

                var matchCandidates = candidateIndex.Select(source);

                var match = _matcher.Decide(
                    new TrackMatchScope(link.TenantId, link.OwnerUserId, link.TargetBackendInstanceId, link.LibraryScopeId, link.ProviderAccountId, 1, snapshot.SnapshotVersion),
                    source,
                    matchCandidates);

                stored = await _trackMatches.RecordDecisionAsync(
                    execution,
                    new MatchDecisionInput(
                        external.Id,
                        match.SelectedLibraryTrackId,
                        match.SelectedLibraryTrackId.HasValue &&
                        candidateById.TryGetValue(match.SelectedLibraryTrackId.Value, out var matchedCand)
                            ? matchedCand.CanonicalRecordingId
                            : null,
                        ToStorageState(match.State),
                        match.Confidence,
                        .88,
                        (stored?.DecisionVersion ?? 0) + 1,
                        external.SnapshotVersion,
                        libraryIndexRevision,
                        TrackMatchDecisionEngine.AlgorithmVersion,
                        link.PolicyVersion,
                        JsonSerializer.Serialize(match.Candidates),
                        JsonSerializer.Serialize(match.Reasons),
                        JsonSerializer.Serialize(match.Warnings)),
                    cancellationToken);
                storedByExternalId[external.Id] = stored;
            }

            allManualOverrides.TryGetValue(external.Id, out var manual);

            var effectiveLibraryTrackId = manual?.Decision == ManualOverrideDecision.Pin
                ? manual.LibraryTrackId
                : manual?.Decision == ManualOverrideDecision.Reject
                    ? null
                    : stored.LibraryTrackId;
            var effectiveState = manual?.Decision switch
            {
                ManualOverrideDecision.Pin => TrackMatchReviewState.Pinned,
                ManualOverrideDecision.Reject => TrackMatchReviewState.Rejected,
                _ => ToReviewState(stored.State)
            };

            // O(1) lookup dictionary instead of O(candidates) linear scan
            var library = effectiveLibraryTrackId.HasValue && candidateById.TryGetValue(effectiveLibraryTrackId.Value, out var libCand)
                ? libCand
                : null;

            decisions.Add(new PersistedPlaylistMatchDecision(entry.Id, external.Id, effectiveState,
                effectiveLibraryTrackId, library?.BackendItemId, library?.BackendInstanceId, stored.Confidence,
                stored.Threshold, stored.DecisionVersion, DeserializeStrings(stored.ReasonsJson), DeserializeStrings(stored.WarningsJson)));
        }

        // Populate decision IDs after repository persistence so every ID is durable.
        foreach (var entry in entries)
        {
            var external = externals[entry.ExternalMetadataSnapshotId];
            if (storedByExternalId.TryGetValue(external.Id, out var stored))
            {
                decisionIds[entry.Id] = stored.Id;
            }
        }

        return (new ImmutablePlaylistSourceSnapshot(snapshot.Id, link.Id, snapshot.ProviderRevision, snapshot.Name,
            entries.Select(entry => new ImmutablePlaylistSourceEntry(entry.Id, entry.SourcePosition,
                entry.ExternalMetadataSnapshotId, externals[entry.ExternalMetadataSnapshotId].ExternalIdHash)),
            snapshot.Description, snapshot.ArtworkReferenceKey), decisions, decisionIds);
    }

    private async Task<PlaylistOrchestrationResult> PersistSuccessAsync(
        ProtocolExecutionContext execution, PlaylistOrchestrationRequest request, PlaylistLinkRecord link,
        PlaylistSourceSnapshotRecord snapshot, PlaylistMaterializationPlan plan,
        IReadOnlyDictionary<Guid, Guid?> decisionIds, BackendPlaylistSnapshot? before,
        BackendPlaylistWriteReceipt receipt, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.PlaylistSyncRuns.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == link.TenantId &&
            item.PlaylistLinkId == link.Id && item.IdempotencyKey == plan.IdempotencyKey, cancellationToken);
        if (existing != null) return new(plan, existing.Id, existing.State, true, true, existing.ConflictCode);
        var state = plan.HasSkips || receipt.UnsupportedMetadataFields.Count > 0
            ? PlaylistSyncState.PartiallySucceeded : PlaylistSyncState.Succeeded;
        var metadataIssue = receipt.UnsupportedMetadataFields.Count == 0
            ? null
            : string.Join(',', receipt.UnsupportedMetadataFields.Order(StringComparer.Ordinal));
        var run = NewRun(request, link, snapshot, plan, state, before?.Fingerprint,
            receipt.Snapshot.Fingerprint, metadataIssue);
        db.PlaylistSyncRuns.Add(run);
        db.PlaylistSyncEntryResults.AddRange(ToRunEntries(link.TenantId, run.Id, plan, decisionIds));
        var included = plan.Entries.Where(item => item.Status == PlaylistPreviewEntryStatus.Included && item.LibraryTrackId.HasValue).ToArray();
        var memberships = await db.PlaylistTargetMemberships.Where(item => item.TenantId == link.TenantId && item.PlaylistLinkId == link.Id).ToListAsync(cancellationToken);
        var membershipByLibraryTrack = memberships.ToDictionary(item => item.LibraryTrackId);
        var includedLibraryTrackIds = included.Select(item => item.LibraryTrackId!.Value).ToHashSet();
        foreach (var entry in included)
        {
            if (!membershipByLibraryTrack.TryGetValue(entry.LibraryTrackId!.Value, out var membership))
            {
                membership = new PlaylistTargetMembershipRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = link.TenantId,
                    PlaylistLinkId = link.Id,
                    LibraryTrackId = entry.LibraryTrackId!.Value,
                    CreatedBySyncRunId = run.Id,
                    TargetEntryId = entry.BackendItemId!,
                    CreatedAt = _clock.UtcNow
                };
                db.PlaylistTargetMemberships.Add(membership);
                membershipByLibraryTrack.Add(membership.LibraryTrackId, membership);
            }
            membership.LastKnownPosition = entry.TargetPosition!.Value; membership.Active = true;
            membership.UpdatedAt = _clock.UtcNow; membership.Revision++;
        }
        if (link.MirrorStaleEntries)
            foreach (var stale in memberships.Where(item => item.Active && !includedLibraryTrackIds.Contains(item.LibraryTrackId)))
            { stale.Active = false; stale.UpdatedAt = _clock.UtcNow; stale.Revision++; }
        var trackedLink = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id && item.TenantId == link.TenantId, cancellationToken);
        trackedLink.TargetPlaylistId = receipt.Snapshot.BackendPlaylistId; trackedLink.UpdatedAt = _clock.UtcNow; trackedLink.Revision++;
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new(plan, run.Id, state, true, false, metadataIssue);
    }

    private async Task<PlaylistOrchestrationResult> RecordFailureAsync(
        ProtocolExecutionContext execution, PlaylistOrchestrationRequest request, PlaylistLinkRecord link,
        PlaylistSourceSnapshotRecord snapshot, PlaylistMaterializationPlan plan,
        IReadOnlyDictionary<Guid, Guid?> decisionIds, PlaylistSyncState state, string code,
        string? targetBefore, bool backendWriteAttempted, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var run = NewRun(request, link, snapshot, plan, state, targetBefore, null, code);
        db.PlaylistSyncRuns.Add(run); db.PlaylistSyncEntryResults.AddRange(ToRunEntries(link.TenantId, run.Id, plan, decisionIds));
        await db.SaveChangesAsync(cancellationToken);
        return new(plan, run.Id, state, backendWriteAttempted, false, code);
    }

    private PlaylistSyncRunRecord NewRun(PlaylistOrchestrationRequest request, PlaylistLinkRecord link,
        PlaylistSourceSnapshotRecord snapshot, PlaylistMaterializationPlan plan, PlaylistSyncState state,
        string? before, string? after, string? conflict = null) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = link.TenantId,
            OwnerUserId = link.OwnerUserId,
            PlaylistLinkId = link.Id,
            PlaylistSourceSnapshotId = snapshot.Id,
            ScheduleId = request.ScheduleId,
            JobId = request.JobId,
            Generation = request.Generation,
            IdempotencyKey = plan.IdempotencyKey,
            RuleVersion = link.RuleVersion,
            MaterializationMode = link.MaterializationMode,
            State = state,
            TargetRevisionBefore = before,
            TargetRevisionAfter = after,
            ConflictCode = conflict,
            StartedAt = _clock.UtcNow,
            CompletedAt = _clock.UtcNow
        };

    private static IEnumerable<PlaylistSyncEntryResultRecord> ToRunEntries(Guid tenantId, Guid runId,
        PlaylistMaterializationPlan plan, IReadOnlyDictionary<Guid, Guid?> decisionIds) => plan.Entries.Select(entry => new PlaylistSyncEntryResultRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PlaylistSyncRunId = runId,
            PlaylistSourceEntryId = entry.SourceEntryId,
            TrackMatchId = decisionIds[entry.SourceEntryId],
            LibraryTrackId = entry.LibraryTrackId,
            SourcePosition = entry.SourcePosition,
            TargetPosition = entry.TargetPosition,
            Outcome = entry.Status switch
            {
                PlaylistPreviewEntryStatus.Included => PlaylistEntryOutcome.Added,
                PlaylistPreviewEntryStatus.Duplicate => PlaylistEntryOutcome.Skipped,
                PlaylistPreviewEntryStatus.Rejected => PlaylistEntryOutcome.Rejected,
                _ => PlaylistEntryOutcome.Skipped
            },
            OutcomeCode = entry.Status.ToString().ToLowerInvariant(),
            DetailsJson = JsonSerializer.Serialize(new { entry.Reasons, entry.Warnings })
        });

    private static LocalTrackMatchCandidate ToCandidate(LibraryTrackRecord item)
    {
        IReadOnlyDictionary<string, string>? providers = null;
        try { providers = JsonSerializer.Deserialize<Dictionary<string, string>>(item.ProviderIdsJson); } catch (JsonException) { }
        return new(item.Id, item.TenantId, item.OwnerUserId, item.BackendInstanceId, item.LibraryScopeId,
            item.BackendItemId, item.CanonicalRecordingId, item.Title, item.Artist, item.Album, item.AlbumArtist,
            (int)Math.Round(item.DurationMilliseconds / 1000d), item.Isrc, item.MusicBrainzRecordingId, null, providers);
    }

    private static TrackMatchState ToStorageState(TrackMatchReviewState state) => state switch
    {
        TrackMatchReviewState.Accepted => TrackMatchState.Accepted,
        TrackMatchReviewState.Pinned => TrackMatchState.Pinned,
        TrackMatchReviewState.Suggested => TrackMatchState.Suggested,
        TrackMatchReviewState.Rejected => TrackMatchState.Rejected,
        TrackMatchReviewState.Ambiguous => TrackMatchState.Ambiguous,
        _ => TrackMatchState.Unresolved
    };
    private static TrackMatchReviewState ToReviewState(TrackMatchState state) => state switch
    {
        TrackMatchState.Accepted => TrackMatchReviewState.Accepted,
        TrackMatchState.Pinned => TrackMatchReviewState.Pinned,
        TrackMatchState.Suggested => TrackMatchReviewState.Suggested,
        TrackMatchState.Rejected => TrackMatchReviewState.Rejected,
        TrackMatchState.Ambiguous => TrackMatchReviewState.Ambiguous,
        _ => TrackMatchReviewState.Unresolved
    };
    private static IReadOnlyList<string> DeserializeStrings(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record PlaylistMaterializationJobPayload(Guid PlaylistLinkId, long Generation, Guid? SourceSnapshotId = null);

public sealed class PlaylistMaterializationJobHandler(
    IDbContextFactory<AllstarrDbContext> factory,
    IPlaylistOrchestrationService orchestration,
    IPlatformClock clock) : IDurableJobHandler
{
    public string JobType => "playlist.materialize";

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        PlaylistMaterializationJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<PlaylistMaterializationJobPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null || payload.Generation <= 0)
        {
            PlaylistSyncScheduledPayload? scheduled;
            try { scheduled = context.Claim.Payload.Deserialize<PlaylistSyncScheduledPayload>(); }
            catch (JsonException) { scheduled = null; }
            if (scheduled != null)
                payload = new PlaylistMaterializationJobPayload(
                    scheduled.PlaylistLinkId,
                    scheduled.ScheduledFor.UtcTicks);
        }
        if (payload == null || payload.PlaylistLinkId == Guid.Empty || payload.Generation <= 0 ||
            !context.Claim.TenantId.HasValue || !context.Claim.OwnerUserId.HasValue)
            return DurableJobCompletion.Failure("playlist_payload_invalid", "The playlist materialization payload is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == payload.PlaylistLinkId &&
            item.TenantId == context.Claim.TenantId && item.OwnerUserId == context.Claim.OwnerUserId, cancellationToken);
        if (link == null) return DurableJobCompletion.Failure("playlist_link_unavailable", "The playlist link is unavailable.");
        if (!link.Enabled) return DurableJobCompletion.Success();
        var identity = await db.BackendIdentities.AsNoTracking().FirstOrDefaultAsync(item => item.TenantId == link.TenantId &&
            item.UserId == link.OwnerUserId && item.BackendType == link.TargetProtocol &&
            item.BackendInstanceId == link.TargetBackendInstanceId, cancellationToken);
        if (identity == null) return DurableJobCompletion.Failure("playlist_backend_identity_unavailable", "The target backend identity is unavailable.");
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == link.OwnerUserId && item.TenantId == link.TenantId, cancellationToken);
        var protocol = link.TargetProtocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic;
        var execution = new ProtocolExecutionContext(protocol, link.TargetBackendInstanceId, identity.PrincipalId,
            new AllstarrPrincipal(link.TenantId, link.OwnerUserId, protocol.ToString().ToLowerInvariant(),
                link.TargetBackendInstanceId, identity.PrincipalId, user.DisplayName, false),
            context.Claim.CorrelationId, clock.UtcNow.AddMinutes(10), cancellationToken, libraryScopeId: link.LibraryScopeId);
        try
        {
            var result = await orchestration.RunAsync(execution,
                new PlaylistOrchestrationRequest(link.Id, payload.Generation, payload.SourceSnapshotId,
                    context.Claim.JobId, link.ScheduleId), cancellationToken);
            return result.State is PlaylistSyncState.Failed or PlaylistSyncState.Conflicted
                ? DurableJobCompletion.Retry(result.ErrorCode ?? "playlist_materialization_failed", "Playlist materialization did not complete.")
                : DurableJobCompletion.Success();
        }
        catch (PlaylistSourceUnavailableException exception)
        { return DurableJobCompletion.Retry(exception.Code, "The playlist source is temporarily unavailable."); }
    }
}

public static class PlaylistOrchestrationRegistration
{
    public static IServiceCollection AddPlaylistOrchestration(this IServiceCollection services)
    {
        services.AddHttpClient(SubsonicPlaylistTarget.HttpClientName);
        services.AddSingleton<IBackendPlaylistAuthenticationResolver, EncryptedSubsonicPlaylistAuthenticationResolver>();
        services.AddSingleton<IBackendPlaylistTarget, JellyfinPlaylistTarget>();
        services.AddSingleton<IBackendPlaylistTarget, SubsonicPlaylistTarget>();
        services.AddSingleton<ProviderPlaylistSnapshotCollector>();
        services.AddSingleton<IProviderPlaylistSourceGateway, ProviderPlaylistSourceGateway>();
        services.AddSingleton<PlaylistMaterializationPlanner>();
        services.AddSingleton<TrackMatchDecisionEngine>();
        services.AddSingleton<IPlaylistVirtualizationService, PlaylistVirtualizationService>();
        services.AddSingleton<allstarr.Core.Protocols.Subsonic.ISubsonicPlaylistMutationResolver,
            allstarr.Core.Protocols.Subsonic.SubsonicPlaylistMutationResolver>();
        services.AddSingleton<allstarr.Core.Protocols.Jellyfin.JellyfinVirtualPlaylistProtocolAdapter>();
        services.AddSingleton<allstarr.Core.Protocols.Subsonic.SubsonicVirtualPlaylistProtocolAdapter>();
        services.AddSingleton<IBackendPlaylistTargetResolver, BackendPlaylistTargetResolver>();
        services.AddSingleton<PlaylistOrchestrationService>();
        services.AddSingleton<DurablePlaylistProjectionReader>();
        services.AddSingleton<IPlaylistOrchestrationService>(provider => provider.GetRequiredService<PlaylistOrchestrationService>());
        services.AddSingleton<IDurableJobHandler, PlaylistMaterializationJobHandler>();
        return services;
    }
}
