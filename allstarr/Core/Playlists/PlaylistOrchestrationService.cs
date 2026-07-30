using System.Diagnostics;
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
    Guid? ScheduleId = null,
    Func<int, int, string, CancellationToken, Task>? Progress = null);

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
    private readonly ILogger<PlaylistOrchestrationService>? _logger;

    public PlaylistOrchestrationService(
        IDbContextFactory<AllstarrDbContext> factory,
        IProviderPlaylistSourceGateway source,
        IBackendPlaylistTargetResolver targets,
        PlaylistMaterializationPlanner planner,
        TrackMatchDecisionEngine matcher,
        ITrackMatchRepository trackMatches,
        IPlatformClock clock,
        ILogger<PlaylistOrchestrationService>? logger = null) =>
        (_factory, _source, _targets, _planner, _matcher, _trackMatches, _clock, _logger) =
        (factory, source, targets, planner, matcher, trackMatches, clock, logger);

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
            : await CollectWithRetentionLogAsync(
                execution, link, request.JobId, cancellationToken);
        var (source, decisions, decisionIds) = await MatchAndLoadAsync(
            execution, link, snapshot, request.Progress, cancellationToken);
        await PublishGenerationAsync(link, snapshot, decisionIds, cancellationToken);
        await LogReconciliationAsync(link, cancellationToken);
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
        var latestPublishedSnapshotId = await initial.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.TenantId == link.TenantId &&
                           item.PlaylistLinkId == link.Id &&
                           item.PublishedAt.HasValue)
            .OrderByDescending(item => item.SnapshotVersion)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var plan = _planner.Plan(
            mode,
            source,
            decisions,
            planningTarget,
            rules,
            latestPublishedSnapshotId);
        if (!plan.RequiresBackendWrite)
            return new(plan, null, null, false, false);

        var claim = await ClaimRunAsync(
            request, link, snapshot, plan, decisionIds, cancellationToken);
        if (!claim.Claimed)
            return new(
                plan, claim.Run.Id, claim.Run.State, false, true, claim.Run.ConflictCode);
        var runId = claim.Run.Id;

        try
        {
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
                    return await RecordFailureAsync(runId, plan,
                        read.Status == BackendPlaylistTargetStatus.Conflict ? PlaylistSyncState.Conflicted : PlaylistSyncState.Failed,
                        read.ErrorCode ?? read.Status.ToString(), before?.Fingerprint, backendWriteAttempted: false,
                        cancellationToken);
            }
            else
            {
                var found = await target.FindByNameAsync(targetContext, snapshot.Name, cancellationToken);
                if (found.IsSuccess) before = found.Value;
                else if (found.Status is not BackendPlaylistTargetStatus.NotFound)
                    return await RecordFailureAsync(runId, plan,
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
                return await RecordFailureAsync(runId, plan,
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
            var verificationRead = await target.ReadAsync(
                targetContext, receipt.Snapshot.BackendPlaylistId, cancellationToken);
            var verificationError = verificationRead.IsSuccess && verificationRead.Value != null
                ? null
                : verificationRead.ErrorCode ?? verificationRead.Status.ToString().ToLowerInvariant();
            if (verificationRead.IsSuccess && verificationRead.Value != null)
                receipt = receipt with { Snapshot = verificationRead.Value };

            return await PersistSuccessAsync(
                runId, link, plan, before, receipt,
                verificationError, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await MarkClaimAsync(runId, PlaylistSyncState.Cancelled, "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            var code = exception.GetType().Name.ToLowerInvariant();
            await MarkClaimAsync(
                runId,
                PlaylistSyncState.Failed,
                code[..Math.Min(100, code.Length)]);
            throw;
        }
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
        var snapshot = await CollectWithRetentionLogAsync(
            execution, link, jobId, cancellationToken);
        var (_, _, decisionIds) = await MatchAndLoadAsync(execution, link, snapshot, null, cancellationToken);
        await PublishGenerationAsync(link, snapshot, decisionIds, cancellationToken);
        await LogReconciliationAsync(link, cancellationToken);
        return new PlaylistRefreshResult(snapshot.Id, snapshot.SnapshotVersion, snapshot.ProviderRevision);
    }

    private async Task LogReconciliationAsync(
        PlaylistLinkRecord link,
        CancellationToken cancellationToken)
    {
        if (_logger == null) return;
        var projection = await new DurablePlaylistProjectionReader(_factory)
            .ReadByLinkIdAsync(
                link.TenantId,
                link.OwnerUserId,
                link.Id,
                cancellationToken);
        var value = projection?.Reconciliation;
        if (projection == null || value == null) return;
        _logger.LogInformation(
            "Playlist reconciliation completed. PlaylistLink: {PlaylistLink}. SnapshotVersion: {SnapshotVersion}. ProviderRows: {ProviderRows}. RawRows: {RawRows}. MappedRows: {MappedRows}. PersistedRows: {PersistedRows}. PublishedRows: {PublishedRows}. Accepted: {Accepted}. Tentative: {Tentative}. Rejected: {Rejected}. Unresolved: {Unresolved}. PlayableRoutes: {PlayableRoutes}. MaterializedRows: {MaterializedRows}. ProtocolVisibleRows: {ProtocolVisibleRows}. AddedPositions: {AddedPositions}. RemovedPositions: {RemovedPositions}. MovedPositions: {MovedPositions}. DuplicatedPositions: {DuplicatedPositions}. ChangedPositions: {ChangedPositions}",
            Hash(link.Id.ToString("N"))[..12],
            projection.SnapshotVersion,
            value.ProviderAdvertisedRows,
            value.RawRows,
            value.MappedRows,
            value.PersistedSourceRows,
            value.PublishedRows,
            value.Accepted,
            value.Tentative,
            value.Rejected,
            value.Unresolved,
            value.PlayableRoutes,
            value.MaterializedTargetRows,
            value.ProtocolVisibleRows,
            string.Join(',', value.AddedPositions),
            string.Join(',', value.RemovedPositions),
            string.Join(',', value.MovedPositions),
            string.Join(',', value.DuplicatedPositions),
            string.Join(',', value.ChangedPositions));
    }

    private async Task<PlaylistSourceSnapshotRecord> CollectWithRetentionLogAsync(
        ProtocolExecutionContext execution,
        PlaylistLinkRecord link,
        Guid? jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CollectAndPersistAsync(
                execution, link, jobId, cancellationToken);
        }
        catch (PlaylistSourceUnavailableException exception)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var retained = await db.PlaylistSourceSnapshots.AsNoTracking()
                .Where(item => item.TenantId == link.TenantId &&
                               item.PlaylistLinkId == link.Id &&
                               item.PublishedAt.HasValue)
                .OrderByDescending(item => item.SnapshotVersion)
                .FirstOrDefaultAsync(cancellationToken);
            var persistedCount = retained == null
                ? 0
                : await db.PlaylistSourceEntries.AsNoTracking().CountAsync(
                    item => item.PlaylistSourceSnapshotId == retained.Id,
                    cancellationToken);
            _logger?.LogWarning(
                "Provider playlist snapshot decision. Account: {Account}. PlaylistLink: {PlaylistLink}. ProviderRevision: {ProviderRevision}. ContentFingerprint: {ContentFingerprint}. SnapshotVersion: {SnapshotVersion}. MappedCount: {MappedCount}. PersistedCount: {PersistedCount}. Decision: {Decision}. ReasonCode: {ReasonCode}",
                Hash(link.ProviderAccountId.ToString("N"))[..12],
                Hash(link.Id.ToString("N"))[..12],
                retained == null ? "none" : Hash(retained.ProviderRevision)[..12],
                retained?.PayloadSha256[..Math.Min(12, retained.PayloadSha256.Length)] ?? "none",
                retained?.SnapshotVersion,
                0,
                persistedCount,
                "retained-last-good",
                exception.Code);
            throw;
        }
    }

    private async Task PublishGenerationAsync(
        PlaylistLinkRecord link,
        PlaylistSourceSnapshotRecord snapshot,
        IReadOnlyDictionary<Guid, Guid?> decisionIds,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var published = await db.PlaylistSourceSnapshots.SingleAsync(item =>
            item.Id == snapshot.Id &&
            item.TenantId == link.TenantId &&
            item.PlaylistLinkId == link.Id,
            cancellationToken);
        var entries = await db.PlaylistSourceEntries
            .Where(item => item.TenantId == link.TenantId &&
                           item.PlaylistSourceSnapshotId == snapshot.Id)
            .ToListAsync(cancellationToken);
        if (entries.Count != decisionIds.Count ||
            entries.Any(item => !decisionIds.TryGetValue(item.Id, out var decisionId) ||
                                !decisionId.HasValue))
            throw new InvalidOperationException("A playlist generation cannot publish without one durable decision per source entry.");
        var matchIds = decisionIds.Values.Select(item => item!.Value).Distinct().ToArray();
        var matches = await db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == link.TenantId && matchIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (matches.Count != matchIds.Length ||
            entries.Any(item =>
                !matches.TryGetValue(decisionIds[item.Id]!.Value, out var match) ||
                match.ExternalSnapshotId != item.ExternalMetadataSnapshotId))
            throw new InvalidOperationException("A playlist generation references an unavailable match decision.");
        foreach (var entry in entries)
            entry.PublishedTrackMatchId = decisionIds[entry.Id];
        published.PublishedAt = _clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<PlaylistSourceSnapshotRecord> CollectAndPersistAsync(
        ProtocolExecutionContext execution, PlaylistLinkRecord link, Guid? jobId, CancellationToken cancellationToken)
    {
        var collected = await _source.CollectAsync(execution, link, cancellationToken);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(CAST({link.ProviderAccountId} AS text), 0))",
            cancellationToken);
        var now = _clock.UtcNow;
        var payloads = collected.Entries.ToDictionary(
            entry => entry.SourceEntryIdHash,
            entry =>
            {
                var payload = JsonSerializer.Serialize(new
                {
                    entry.ProviderTrackIdHash,
                    entry.Title,
                    entry.Artists,
                    entry.Album,
                    entry.DurationMilliseconds,
                    durationProvenance = entry.DurationMilliseconds.HasValue ? link.SourceProviderId : null,
                    entry.Isrc,
                    entry.IsExplicit,
                    entry.ArtworkUrl,
                    entry.CanonicalRecordingId
                });
                return (Payload: payload, PayloadHash: Hash(payload));
            },
            StringComparer.Ordinal);
        var playlistPayload = JsonSerializer.Serialize(new
        {
            collected.Name,
            collected.Description,
            collected.ArtworkReferenceKey,
            entries = collected.Entries.Select(item => new
            {
                item.SourcePosition,
                item.SourceEntryIdHash,
                item.ProviderTrackIdHash,
                metadataSha256 = payloads[item.SourceEntryIdHash].PayloadHash
            })
        });
        var playlistPayloadHash = Hash(playlistPayload);
        var existing = await db.PlaylistSourceSnapshots.AsNoTracking().Where(item =>
                item.TenantId == link.TenantId && item.PlaylistLinkId == link.Id &&
                item.PayloadSha256 == playlistPayloadHash)
            .OrderByDescending(item => item.SnapshotVersion).FirstOrDefaultAsync(cancellationToken);
        if (existing != null && await db.PlaylistSourceEntries.AsNoTracking()
                .Where(item => item.PlaylistSourceSnapshotId == existing.Id)
                .AllAsync(item => db.ExternalMetadataSnapshots.Any(external =>
                    external.Id == item.ExternalMetadataSnapshotId &&
                    external.OwnerUserId == link.OwnerUserId &&
                    external.LibraryScopeId == link.LibraryScopeId &&
                    external.BackendInstanceId == link.TargetBackendInstanceId &&
                    external.Protocol == link.TargetProtocol), cancellationToken))
        {
            _logger?.LogInformation(
                "Provider playlist snapshot decision. Account: {Account}. PlaylistLink: {PlaylistLink}. ProviderRevision: {ProviderRevision}. ContentFingerprint: {ContentFingerprint}. SnapshotVersion: {SnapshotVersion}. MappedCount: {MappedCount}. PersistedCount: {PersistedCount}. Decision: {Decision}",
                Hash(link.ProviderAccountId.ToString("N"))[..12],
                Hash(link.Id.ToString("N"))[..12],
                Hash(collected.SourceRevision)[..12],
                playlistPayloadHash[..12],
                existing.SnapshotVersion,
                collected.Entries.Count,
                collected.Entries.Count,
                "reused");
            return existing;
        }
        var version = (await db.PlaylistSourceSnapshots.Where(item =>
                item.TenantId == link.TenantId && item.PlaylistLinkId == link.Id)
            .MaxAsync(item => (int?)item.SnapshotVersion, cancellationToken) ?? 0) + 1;
        var externalBySourceEntry = new Dictionary<string, ExternalMetadataSnapshotRecord>(StringComparer.Ordinal);
        var storedExternals = new List<ExternalMetadataSnapshotRecord>();
        var providerTrackHashes = collected.Entries
            .Select(item => item.ProviderTrackIdHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var hashes in providerTrackHashes.Chunk(500))
        {
            storedExternals.AddRange(await db.ExternalMetadataSnapshots.AsNoTracking()
                .Where(item => item.TenantId == link.TenantId &&
                               item.ProviderAccountId == link.ProviderAccountId &&
                               item.ResourceKind == "track" &&
                               hashes.Contains(item.ExternalIdHash))
                .ToListAsync(cancellationToken));
        }
        var exactExternals = storedExternals
            .Where(item => item.OwnerUserId == link.OwnerUserId &&
                           item.LibraryScopeId == link.LibraryScopeId &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           item.Protocol == link.TargetProtocol &&
                           item.PayloadSha256 != null)
            .GroupBy(item => (item.ExternalIdHash, item.PayloadSha256))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.SnapshotVersion).First());
        var latestVersions = storedExternals
            .GroupBy(item => item.ExternalIdHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.SnapshotVersion), StringComparer.Ordinal);
        var providerIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item =>
                item.TenantId == link.TenantId &&
                item.ProviderId == link.SourceProviderId &&
                item.ResourceKind == ProviderResourceKind.Track &&
                providerTrackHashes.Contains(item.ExternalIdHash) &&
                (item.Verification == ProviderIdentityVerification.Verified ||
                 item.Verification == ProviderIdentityVerification.Pinned))
            .ToListAsync(cancellationToken);
        var providerIdentityByHash = providerIdentities
            .GroupBy(item => item.ExternalIdHash, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
                    .ThenByDescending(item => item.VerifiedAt)
                    .First(),
                StringComparer.Ordinal);
        var newExternals = new Dictionary<(string Track, string Payload), ExternalMetadataSnapshotRecord>();
        foreach (var entry in collected.Entries.OrderBy(item => item.SourcePosition))
        {
            var (payload, payloadHash) = payloads[entry.SourceEntryIdHash];
            var contentKey = (entry.ProviderTrackIdHash, payloadHash);
            exactExternals.TryGetValue(contentKey, out var external);
            external ??= newExternals.GetValueOrDefault(contentKey);
            if (external == null)
            {
                var externalVersion = latestVersions.GetValueOrDefault(entry.ProviderTrackIdHash) + 1;
                external = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = link.TenantId,
                    OwnerUserId = link.OwnerUserId,
                    ProviderAccountId = link.ProviderAccountId,
                    ProviderTrackIdentityId = providerIdentityByHash
                        .GetValueOrDefault(entry.ProviderTrackIdHash)?.Id,
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
                newExternals[contentKey] = external;
                latestVersions[entry.ProviderTrackIdHash] = externalVersion;
            }
            externalBySourceEntry[entry.SourceEntryIdHash] = external;
        }
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
            PayloadSha256 = playlistPayloadHash,
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
        await transaction.CommitAsync(cancellationToken);
        _logger?.LogInformation(
            "Provider playlist snapshot decision. Account: {Account}. PlaylistLink: {PlaylistLink}. ProviderRevision: {ProviderRevision}. ContentFingerprint: {ContentFingerprint}. SnapshotVersion: {SnapshotVersion}. MappedCount: {MappedCount}. PersistedCount: {PersistedCount}. Decision: {Decision}",
            Hash(link.ProviderAccountId.ToString("N"))[..12],
            Hash(link.Id.ToString("N"))[..12],
            Hash(collected.SourceRevision)[..12],
            playlistPayloadHash[..12],
            snapshot.SnapshotVersion,
            collected.Entries.Count,
            collected.Entries.Count,
            "created");
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
        Func<int, int, string, CancellationToken, Task>? progress,
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
        var providerIdentityIds = externals.Values
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var providerIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == link.TenantId &&
                           providerIdentityIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var candidates = await db.LibraryTracks.AsNoTracking()
            .Where(item =>
                item.TenantId == link.TenantId && item.OwnerUserId == link.OwnerUserId &&
                item.LibraryScopeId == link.LibraryScopeId &&
                item.BackendInstanceId == link.TargetBackendInstanceId)
            .Select(item => new LibraryTrackRecord
            {
                Id = item.Id,
                TenantId = item.TenantId,
                OwnerUserId = item.OwnerUserId,
                CanonicalRecordingId = item.CanonicalRecordingId,
                LibraryScopeId = item.LibraryScopeId,
                BackendInstanceId = item.BackendInstanceId,
                BackendItemId = item.BackendItemId,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                AlbumArtist = item.AlbumArtist,
                DurationMilliseconds = item.DurationMilliseconds,
                Isrc = item.Isrc,
                MusicBrainzRecordingId = item.MusicBrainzRecordingId,
                ProviderIdsJson = item.ProviderIdsJson
            })
            .ToListAsync(cancellationToken);
        var candidateIds = candidates.Select(item => item.Id).ToHashSet();
        var candidateDecisions = db.TrackMatches.AsNoTracking()
            .Where(item =>
                item.TenantId == link.TenantId &&
                item.OwnerUserId == link.OwnerUserId &&
                item.LibraryScopeId == link.LibraryScopeId &&
                item.CanonicalRecordingId.HasValue &&
                item.LibraryTrackId.HasValue &&
                candidateIds.Contains(item.LibraryTrackId.Value));
        var latestCandidateVersions = candidateDecisions
            .GroupBy(item => item.ExternalSnapshotId)
            .Select(group => new
            {
                ExternalSnapshotId = group.Key,
                DecisionVersion = group.Max(item => item.DecisionVersion)
            });
        var latestCandidateDecisions =
            from decision in candidateDecisions
            join version in latestCandidateVersions
                on new { decision.ExternalSnapshotId, decision.DecisionVersion }
                equals new { version.ExternalSnapshotId, version.DecisionVersion }
            select decision;
        var priorAccepted = (await latestCandidateDecisions.ToListAsync(cancellationToken))
            .Where(item => item.State is TrackMatchState.Accepted or TrackMatchState.Pinned)
            .OrderByDescending(item => item.DecidedAt)
            .GroupBy(item => item.LibraryTrackId!.Value)
            .ToDictionary(group => group.Key, group => group.First().CanonicalRecordingId!.Value);
        var identityCanonicalByProviderTrack = providerIdentities.Values
            .SelectMany(identity => new[]
            {
                new { Key = $"{identity.ProviderId}:{identity.ExternalId}", identity.CanonicalRecordingId },
                new { Key = $"{identity.ProviderId}:{identity.ExternalIdHash}", identity.CanonicalRecordingId }
            })
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().CanonicalRecordingId,
                StringComparer.OrdinalIgnoreCase);

        // Optimization: Map candidates once outside the loop instead of doing it N times
        var mappedCandidates = candidates.Select(ToCandidate)
            .Select(candidate =>
            {
                var canonicalRecordingId = candidate.ProviderTrackIds?
                    .Select(item => identityCanonicalByProviderTrack.TryGetValue(
                        $"{item.Key}:{item.Value}", out var identityCanonical)
                            ? (Guid?)identityCanonical
                            : null)
                    .FirstOrDefault(item => item.HasValue);
                if (!canonicalRecordingId.HasValue &&
                    priorAccepted.TryGetValue(candidate.LibraryTrackId, out var acceptedCanonical))
                    canonicalRecordingId = acceptedCanonical;
                return !candidate.CanonicalRecordingId.HasValue && canonicalRecordingId.HasValue
                    ? candidate with { CanonicalRecordingId = canonicalRecordingId }
                    : candidate;
            })
            .ToArray();
        var candidateSet = _matcher.PrepareCandidates(mappedCandidates);
        var libraryIndexRevision = candidateSet.Revision;
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

        var pendingDecisions = new Dictionary<Guid, MatchDecisionInput>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var external = externals[entry.ExternalMetadataSnapshotId];
            if (pendingDecisions.ContainsKey(external.Id))
                continue;
            allManualOverrides.TryGetValue(external.Id, out var manual);
            storedByExternalId.TryGetValue(external.Id, out var stored);
            if (stored != null &&
                stored.SourceSnapshotVersion == external.SnapshotVersion &&
                stored.LibraryIndexRevision == libraryIndexRevision &&
                stored.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion &&
                stored.PolicyVersion == link.PolicyVersion)
                continue;

            using var payload = JsonDocument.Parse(external.PayloadJson);
            var root = payload.RootElement;
            var artists = root.GetProperty("Artists").EnumerateArray().Select(item => item.GetString()).Where(item => item != null).ToArray();
            var canonicalRecordingId = external.ProviderTrackIdentityId.HasValue &&
                                       providerIdentities.TryGetValue(
                                           external.ProviderTrackIdentityId.Value, out var providerIdentity)
                ? providerIdentity.CanonicalRecordingId
                : root.TryGetProperty("CanonicalRecordingId", out var canonical) &&
                  canonical.ValueKind == JsonValueKind.String &&
                  canonical.TryGetGuid(out var parsedCanonical)
                    ? parsedCanonical
                    : (Guid?)null;
            var source = new ExternalTrackMatchSnapshot(external.Id.ToString("N"), link.SourceProviderId,
                external.ExternalIdHash, root.TryGetProperty("Title", out var title) ? title.GetString() ?? "Unknown" : "Unknown",
                artists.Length > 0 ? string.Join(", ", artists) : "Unknown",
                root.TryGetProperty("Album", out var album) ? album.GetString() : null, null,
                ReadDurationMilliseconds(root),
                root.TryGetProperty("Isrc", out var isrc) ? isrc.GetString() : null, null,
                root.TryGetProperty("IsExplicit", out var explicitValue) && explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? explicitValue.GetBoolean() : null,
                canonicalRecordingId);

            var rejectedOverride =
                manual?.Decision == ManualOverrideDecision.Reject &&
                manual.LibraryTrackId.HasValue &&
                manual.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion
                    ? new ScopedTrackMatchOverride(
                        link.TenantId,
                        link.OwnerUserId,
                        link.LibraryScopeId,
                        source.ProviderId,
                        source.ExternalId,
                        null,
                        new HashSet<Guid> { manual.LibraryTrackId.Value })
                    : null;

            var match = _matcher.Decide(
                new TrackMatchScope(link.TenantId, link.OwnerUserId, link.TargetBackendInstanceId, link.LibraryScopeId, link.ProviderAccountId, 1, snapshot.SnapshotVersion),
                source,
                candidateSet,
                rejectedOverride);
            var matchedCanonicalRecordingId = match.SelectedLibraryTrackId.HasValue &&
                                              candidateById.TryGetValue(match.SelectedLibraryTrackId.Value, out var matchedCandidate)
                ? canonicalRecordingId ?? matchedCandidate.CanonicalRecordingId
                : null;
            pendingDecisions[external.Id] = MatchDecisionInput.FromDecision(
                external.Id,
                matchedCanonicalRecordingId,
                match,
                (stored?.DecisionVersion ?? 0) + 1,
                external.SnapshotVersion,
                libraryIndexRevision,
                link.PolicyVersion);
        }

        if (pendingDecisions.Count > 0)
        {
            var storedDecisions = await _trackMatches.RecordDecisionsAsync(
                execution, pendingDecisions.Values, cancellationToken);
            foreach (var stored in storedDecisions)
                storedByExternalId[stored.ExternalSnapshotId] = stored;
        }

        var rematchedExternal = false;
        if (_trackMatches.SupportsExternalMatching)
        {
            var pendingExternal = storedByExternalId.Values.Where(item =>
                    item.State is TrackMatchState.Unresolved or
                        TrackMatchState.Suggested or
                        TrackMatchState.Ambiguous)
                .ToArray();
            foreach (var stored in pendingExternal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (allManualOverrides.TryGetValue(stored.ExternalSnapshotId, out var manual) &&
                    (manual.Decision == ManualOverrideDecision.Pin ||
                     manual.Decision == ManualOverrideDecision.Reject && !manual.LibraryTrackId.HasValue))
                    continue;
                var rematch = await _trackMatches.RematchSnapshotAsync(
                    execution,
                    stored.ExternalSnapshotId,
                    execution.CorrelationId,
                    link.PolicyVersion,
                    cancellationToken);
                if (rematch.Succeeded)
                    rematchedExternal = true;
            }
        }
        if (rematchedExternal)
        {
            resolution = await _trackMatches.GetResolutionDataAsync(
                new TrackMatchActor(
                    actor.TenantId,
                    actor.EffectiveUserId ?? link.OwnerUserId,
                    actor.Kind == ProviderActorKind.Administrator),
                link.OwnerUserId,
                link.LibraryScopeId,
                externalIds,
                cancellationToken);
            storedByExternalId = resolution.LatestDecisions
                .ToDictionary(item => item.ExternalSnapshotId);
        }

        var decisions = new List<PersistedPlaylistMatchDecision>(entries.Count);
        var decisionIds = new Dictionary<Guid, Guid?>(entries.Count);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var external = externals[entry.ExternalMetadataSnapshotId];
            allManualOverrides.TryGetValue(external.Id, out var manual);
            var stored = storedByExternalId[external.Id];

            var classification = TrackClassifier.Classify(
                manual,
                stored,
                playableLibraryTrackIds: candidateIds);
            var effectiveLibraryTrackId = classification.LibraryTrackId;
            var effectiveState = ToReviewState(classification.State);

            // O(1) lookup dictionary instead of O(candidates) linear scan
            var library = effectiveLibraryTrackId.HasValue && candidateById.TryGetValue(effectiveLibraryTrackId.Value, out var libCand)
                ? libCand
                : null;

            decisions.Add(new PersistedPlaylistMatchDecision(entry.Id, external.Id, effectiveState,
                effectiveLibraryTrackId, library?.BackendItemId, library?.BackendInstanceId, stored.Confidence,
                stored.Threshold, stored.DecisionVersion, DeserializeStrings(stored.ReasonsJson), DeserializeStrings(stored.WarningsJson)));
            if (progress != null)
            {
                using var payload = JsonDocument.Parse(external.PayloadJson);
                var title = payload.RootElement.TryGetProperty("Title", out var value)
                    ? value.GetString() ?? "Unknown track"
                    : "Unknown track";
                await progress(decisions.Count, entries.Count, title, cancellationToken);
            }
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

    private async Task<(PlaylistSyncRunRecord Run, bool Claimed)> ClaimRunAsync(
        PlaylistOrchestrationRequest request,
        PlaylistLinkRecord link,
        PlaylistSourceSnapshotRecord snapshot,
        PlaylistMaterializationPlan plan,
        IReadOnlyDictionary<Guid, Guid?> decisionIds,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.PlaylistSyncRuns.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == link.TenantId &&
            item.PlaylistLinkId == link.Id &&
            item.IdempotencyKey == plan.IdempotencyKey,
            cancellationToken);
        if (existing != null)
            return await ReclaimRunAsync(db, existing, request, snapshot, plan, cancellationToken);

        var run = NewRun(
            request, link, snapshot, plan, PlaylistSyncState.Running, null, null);
        db.PlaylistSyncRuns.Add(run);
        db.PlaylistSyncEntryResults.AddRange(
            ToRunEntries(link.TenantId, run.Id, plan, decisionIds));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return (run, true);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.PlaylistSyncRuns.AsNoTracking().SingleAsync(item =>
                item.TenantId == link.TenantId &&
                item.PlaylistLinkId == link.Id &&
                item.IdempotencyKey == plan.IdempotencyKey,
                cancellationToken);
            return await ReclaimRunAsync(
                db, winner, request, snapshot, plan, cancellationToken);
        }
    }

    private async Task<(PlaylistSyncRunRecord Run, bool Claimed)> ReclaimRunAsync(
        AllstarrDbContext db,
        PlaylistSyncRunRecord run,
        PlaylistOrchestrationRequest request,
        PlaylistSourceSnapshotRecord snapshot,
        PlaylistMaterializationPlan plan,
        CancellationToken cancellationToken)
    {
        if (run.PlaylistSourceSnapshotId != snapshot.Id ||
            run.Generation != request.Generation ||
            run.RuleVersion != plan.Rules.RuleVersion ||
            run.MaterializationMode != (plan.Mode == PlaylistPlanMode.Recreate
                ? PlaylistMaterializationMode.Recreate
                : PlaylistMaterializationMode.Reconcile))
            throw new InvalidOperationException(
                "The sync-run idempotency key already belongs to different inputs.");
        if (run.State is not (PlaylistSyncState.Failed or PlaylistSyncState.Conflicted or
            PlaylistSyncState.Cancelled))
            return (run, false);

        var claimed = await db.PlaylistSyncRuns
            .Where(item => item.Id == run.Id &&
                           (item.State == PlaylistSyncState.Failed ||
                            item.State == PlaylistSyncState.Conflicted ||
                            item.State == PlaylistSyncState.Cancelled))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, PlaylistSyncState.Running)
                .SetProperty(item => item.JobId, request.JobId)
                .SetProperty(item => item.ScheduleId, request.ScheduleId)
                .SetProperty(item => item.ConflictCode, (string?)null)
                .SetProperty(item => item.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(item => item.StartedAt, _clock.UtcNow)
                .SetProperty(item => item.Revision, item => item.Revision + 1),
                cancellationToken);
        var current = await db.PlaylistSyncRuns.AsNoTracking()
            .SingleAsync(item => item.Id == run.Id, cancellationToken);
        return (current, claimed == 1);
    }

    private async Task<PlaylistOrchestrationResult> PersistSuccessAsync(
        Guid runId, PlaylistLinkRecord link, PlaylistMaterializationPlan plan,
        BackendPlaylistSnapshot? before,
        BackendPlaylistWriteReceipt receipt, string? verificationError,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await db.PlaylistSyncRuns.SingleAsync(item =>
            item.Id == runId && item.TenantId == link.TenantId &&
            item.PlaylistLinkId == link.Id && item.State == PlaylistSyncState.Running,
            cancellationToken);
        var memberships = await db.PlaylistTargetMemberships
            .Where(item => item.TenantId == link.TenantId && item.PlaylistLinkId == link.Id)
            .ToListAsync(cancellationToken);
        var verification = await VerifyMaterializationAsync(
            db, link, plan, before, receipt.Snapshot, memberships, verificationError,
            cancellationToken);
        var state = plan.HasSkips ||
                    receipt.UnsupportedMetadataFields.Count > 0 ||
                    verification.Code != "verified"
            ? PlaylistSyncState.PartiallySucceeded
            : PlaylistSyncState.Succeeded;
        var metadataIssue = receipt.UnsupportedMetadataFields.Count == 0
            ? null
            : string.Join(',', receipt.UnsupportedMetadataFields.Order(StringComparer.Ordinal));
        run.State = state;
        run.TargetRevisionBefore = before?.Fingerprint;
        run.TargetRevisionAfter = receipt.Snapshot.Fingerprint;
        run.ConflictCode = metadataIssue;
        run.PlannedTargetTrackCount = verification.PlannedTrackCount;
        run.PlannedTargetDurationMilliseconds = verification.PlannedDurationMilliseconds;
        run.VerifiedTargetTrackCount = verification.VerifiedTrackCount;
        run.VerifiedTargetDurationMilliseconds = verification.VerifiedDurationMilliseconds;
        run.VerificationCode = verification.Code;
        run.VerifiedAt = _clock.UtcNow;
        run.CompletedAt = _clock.UtcNow;
        run.Revision++;
        var included = plan.Entries.Where(item => item.Status == PlaylistPreviewEntryStatus.Included && item.LibraryTrackId.HasValue).ToArray();
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
        return new(plan, run.Id, state, true, false,
            verification.Code == "verified" ? metadataIssue : verification.Code);
    }

    private static async Task<MaterializationVerification> VerifyMaterializationAsync(
        AllstarrDbContext db,
        PlaylistLinkRecord link,
        PlaylistMaterializationPlan plan,
        BackendPlaylistSnapshot? before,
        BackendPlaylistSnapshot after,
        IReadOnlyCollection<PlaylistTargetMembershipRecord> memberships,
        string? verificationError,
        CancellationToken cancellationToken)
    {
        var planned = plan.OrderedBackendItemIds.ToHashSet(StringComparer.Ordinal);
        var syncOwned = memberships
            .Where(item => item.Active)
            .Select(item => item.TargetEntryId)
            .ToHashSet(StringComparer.Ordinal);
        var expectedIds = plan.OrderedBackendItemIds
            .Concat((before?.Members ?? [])
                .Select(item => item.BackendItemId)
                .Where(item => !planned.Contains(item))
                .Where(item => !link.MirrorStaleEntries || !syncOwned.Contains(item)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var durations = await db.LibraryTracks.AsNoTracking()
            .Where(item =>
                item.TenantId == link.TenantId &&
                item.OwnerUserId == link.OwnerUserId &&
                item.LibraryScopeId == link.LibraryScopeId &&
                item.BackendInstanceId == link.TargetBackendInstanceId &&
                expectedIds.Contains(item.BackendItemId))
            .Select(item => new { item.BackendItemId, item.DurationMilliseconds })
            .ToListAsync(cancellationToken);
        var indexedDurations = durations
            .GroupBy(item => item.BackendItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DurationMilliseconds,
                StringComparer.Ordinal);
        var priorDurations = (before?.Members ?? [])
            .Where(item => item.DurationMilliseconds.HasValue)
            .GroupBy(item => item.BackendItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().DurationMilliseconds,
                StringComparer.Ordinal);
        var expectedDurationParts = expectedIds.Select(item =>
            indexedDurations.GetValueOrDefault(item) ??
            priorDurations.GetValueOrDefault(item)).ToArray();
        var plannedDuration = expectedIds.Length == 0
            ? 0
            : expectedDurationParts.All(item => item.HasValue)
                ? expectedDurationParts.Sum(item => item!.Value)
                : (long?)null;
        var verifiedCount = after.ReportedTrackCount is >= 0
            ? after.ReportedTrackCount.Value
            : after.Members.Count;
        var verifiedDuration = after.DurationMilliseconds is >= 0
            ? after.DurationMilliseconds
            : after.Members.Count == 0
                ? 0
                : after.Members.All(item => item.DurationMilliseconds.HasValue)
                    ? after.Members.Sum(item => item.DurationMilliseconds!.Value)
                    : null;
        if (verificationError != null)
        {
            var readErrorCode = $"verification_read_{verificationError}";
            return new(expectedIds.Length, plannedDuration, null, null,
                readErrorCode[..Math.Min(100, readErrorCode.Length)]);
        }
        var countMismatch = expectedIds.Length != verifiedCount;
        var durationUnavailable = !plannedDuration.HasValue || !verifiedDuration.HasValue;
        var durationMismatch = !durationUnavailable &&
                               Math.Abs(plannedDuration!.Value - verifiedDuration!.Value) >
                               Math.Max(1000L, expectedIds.LongLength * 1000L);
        var code = (countMismatch, durationUnavailable, durationMismatch) switch
        {
            (false, false, false) => "verified",
            (true, true, _) => "count_mismatch_duration_unavailable",
            (false, true, _) => "duration_unavailable",
            (true, false, true) => "count_and_duration_mismatch",
            (true, false, false) => "count_mismatch",
            _ => "duration_mismatch"
        };
        return new(expectedIds.Length, plannedDuration, verifiedCount, verifiedDuration, code);
    }

    private sealed record MaterializationVerification(
        int PlannedTrackCount,
        long? PlannedDurationMilliseconds,
        int? VerifiedTrackCount,
        long? VerifiedDurationMilliseconds,
        string Code);

    private async Task<PlaylistOrchestrationResult> RecordFailureAsync(
        Guid runId, PlaylistMaterializationPlan plan, PlaylistSyncState state, string code,
        string? targetBefore, bool backendWriteAttempted, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var run = await db.PlaylistSyncRuns.SingleAsync(item =>
            item.Id == runId && item.State == PlaylistSyncState.Running,
            cancellationToken);
        run.State = state;
        run.TargetRevisionBefore = targetBefore;
        run.ConflictCode = code;
        run.CompletedAt = _clock.UtcNow;
        run.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return new(plan, runId, state, backendWriteAttempted, false, code);
    }

    private async Task MarkClaimAsync(
        Guid runId,
        PlaylistSyncState state,
        string code)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.PlaylistSyncRuns
            .Where(item => item.Id == runId && item.State == PlaylistSyncState.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, state)
                .SetProperty(item => item.ConflictCode, code)
                .SetProperty(item => item.CompletedAt, _clock.UtcNow)
                .SetProperty(item => item.Revision, item => item.Revision + 1));
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
            CompletedAt = state is PlaylistSyncState.Pending or PlaylistSyncState.Running
                ? null
                : _clock.UtcNow
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
            item.DurationMilliseconds,
            item.Isrc, item.MusicBrainzRecordingId, null, providers);
    }

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
    private static long? ReadDurationMilliseconds(JsonElement root)
    {
        if ((root.TryGetProperty("DurationMilliseconds", out var value) ||
             root.TryGetProperty("durationMilliseconds", out value)) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var milliseconds))
            return milliseconds;
        return root.TryGetProperty("durationSeconds", out value) && value.TryGetDouble(out var seconds)
            ? checked((long)Math.Round(seconds * 1000d))
            : null;
    }
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
        var playlistName = await db.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id)
            .OrderByDescending(item => item.SnapshotVersion)
            .Select(item => item.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? link.SourcePlaylistId;
        var started = Stopwatch.GetTimestamp();
        await context.ReportProgressAsync(
            new("playlist.prepare", "Preparing playlist synchronization.",
                Provider: link.SourceProviderId, Playlist: playlistName), cancellationToken);
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
            await context.ReportProgressAsync(
                new("playlist.match", "Matching and materializing playlist tracks.",
                    Provider: link.SourceProviderId, Playlist: playlistName), cancellationToken);
            var result = await orchestration.RunAsync(execution,
                new PlaylistOrchestrationRequest(link.Id, payload.Generation, payload.SourceSnapshotId,
                    context.Claim.JobId, link.ScheduleId,
                    async (completed, total, track, token) =>
                    {
                        await context.ReportProgressAsync(
                            new("playlist.match", $"Matched {track}.", completed, total,
                                link.SourceProviderId, playlistName, track,
                                ThroughputPerSecond: completed / Math.Max(
                                    Stopwatch.GetElapsedTime(started).TotalSeconds, .001)),
                            token);
                    }), cancellationToken);
            var completed = result.Plan?.Entries.Count;
            var retry = result.State is PlaylistSyncState.Failed or PlaylistSyncState.Conflicted;
            await context.ReportProgressAsync(
                new(retry ? "playlist.retry" : "playlist.complete",
                    retry ? "Playlist synchronization requires retry." : "Playlist synchronization completed.",
                    completed, completed, link.TargetProtocol, playlistName,
                    ThroughputPerSecond: completed / Math.Max(
                        Stopwatch.GetElapsedTime(started).TotalSeconds, .001)), cancellationToken);
            return retry
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
        services.AddSingleton<allstarr.Core.Protocols.Jellyfin.IJellyfinPlaylistMutationResolver,
            allstarr.Core.Protocols.Jellyfin.JellyfinPlaylistMutationResolver>();
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
