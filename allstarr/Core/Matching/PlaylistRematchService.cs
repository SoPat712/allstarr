using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record PlaylistRematchTarget(
    Guid ExternalSnapshotId,
    int SnapshotVersion,
    int DecisionVersion,
    Guid PlaylistLinkId,
    long PlaylistLinkRevision,
    string PolicyVersion,
    string LibraryScopeId,
    string BackendInstanceId,
    string TargetProtocol);

public sealed record PlaylistRematchPreview(
    string ConfirmationId,
    string ScopeFingerprint,
    int PlaylistCount,
    int LibraryCount,
    int TotalRows,
    int LocalRows,
    int ExactProviderRows,
    int GenericExternalRows,
    int UnresolvedRows,
    int ConfirmedManualRows,
    int StaleRevisionRows,
    int ConflictingRows,
    int RowsToRematch,
    int UniqueTracksToRematch,
    IReadOnlyList<PlaylistRematchTarget> Targets)
{
    public bool CanApply => Targets.Count > 0;
}

public sealed class PlaylistRematchService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurablePlaylistProjectionReader projections,
    TrackMatchDecisionEngine decisionEngine)
{
    private static readonly HashSet<string> GenericProviders =
        new(StringComparer.OrdinalIgnoreCase) { "ext", "external", "unknown", "legacy" };

    public async Task<PlaylistRematchPreview> PreviewAsync(
        Guid tenantId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var links = await db.PlaylistLinks.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.OwnerUserId == ownerUserId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var byLink = await projections.ReadByLinkIdsAsync(
            tenantId, ownerUserId, links.Select(item => item.Id).ToArray(), cancellationToken);
        links = links.Where(item => byLink.ContainsKey(item.Id)).ToList();
        if (links.Count == 0)
            return Empty();

        var rows = links.SelectMany(link => byLink[link.Id].Entries.Select(entry =>
            new PreviewRow(link, entry))).ToArray();
        var externalIds = rows.Select(item => item.Entry.ExternalSnapshotId).Distinct().ToArray();
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => externalIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var latest = await LatestDecisions(db.TrackMatches.AsNoTracking()
                .Where(item => item.TenantId == tenantId && externalIds.Contains(item.ExternalSnapshotId)))
            .ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
        var overrides = await db.ManualTrackOverrides.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           externalIds.Contains(item.ExternalSnapshotId) &&
                           item.RevokedAt == null)
            .ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
        var libraryTracks = await db.LibraryTracks.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.OwnerUserId == ownerUserId)
            .ToListAsync(cancellationToken);
        var libraryRevisions = links.ToDictionary(
            item => item.Id,
            item => decisionEngine.PrepareCandidates(libraryTracks
                .Where(track => track.LibraryScopeId == item.LibraryScopeId &&
                                track.BackendInstanceId == item.TargetBackendInstanceId)
                .Select(ToCandidate)).Revision);

        var contextConflicts = rows
            .GroupBy(item => item.Entry.ExternalSnapshotId)
            .Where(group => group.Select(item => new
            {
                item.Link.Id,
                item.Link.PolicyVersion,
                item.Link.LibraryScopeId,
                item.Link.TargetBackendInstanceId
            }).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var staleIds = new HashSet<Guid>();
        var targetIds = new HashSet<Guid>();
        var targets = new List<PlaylistRematchTarget>();
        foreach (var group in rows.GroupBy(item => item.Entry.ExternalSnapshotId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = group.First();
            if (!snapshots.TryGetValue(group.Key, out var snapshot))
                continue;
            latest.TryGetValue(group.Key, out var decision);
            var libraryRevision = libraryRevisions[row.Link.Id];
            var stale = decision != null &&
                (decision.SourceSnapshotVersion != snapshot.SnapshotVersion ||
                 decision.LibraryIndexRevision != libraryRevision ||
                 decision.MatcherVersion != TrackMatchDecisionEngine.AlgorithmVersion ||
                 decision.PolicyVersion != row.Link.PolicyVersion);
            if (stale) staleIds.Add(group.Key);
            var missingProviderIdentity = group.All(item => item.Entry.ProviderRoutes.Count == 0);
            if (overrides.ContainsKey(group.Key) || contextConflicts.Contains(group.Key) ||
                decision != null && !stale && !missingProviderIdentity)
                continue;
            targetIds.Add(group.Key);
            targets.Add(new(
                group.Key,
                snapshot.SnapshotVersion,
                decision?.DecisionVersion ?? 0,
                row.Link.Id,
                row.Link.Revision,
                row.Link.PolicyVersion,
                row.Link.LibraryScopeId,
                row.Link.TargetBackendInstanceId,
                row.Link.TargetProtocol));
        }

        var scopeFingerprint = Hash(string.Join('\n', links.Select(link =>
                $"link:{link.Id:N}:{link.Revision}:{byLink[link.Id].SnapshotId:N}:{byLink[link.Id].SnapshotVersion}")
            .Concat(links.Select(link => $"library:{link.Id:N}:{libraryRevisions[link.Id]}"))));
        var confirmationId = Hash(string.Join('\n', new[] { scopeFingerprint }
            .Concat(externalIds.Order().Select(id =>
                $"row:{id:N}:{latest.GetValueOrDefault(id)?.DecisionVersion ?? 0}:" +
                $"{latest.GetValueOrDefault(id)?.Revision ?? 0}:" +
                $"{overrides.GetValueOrDefault(id)?.Revision ?? 0}:{targetIds.Contains(id)}"))));
        var conflictingIds = contextConflicts
            .Concat(latest.Where(item => item.Value.State == TrackMatchState.Ambiguous).Select(item => item.Key))
            .ToHashSet();

        return new(
            confirmationId,
            scopeFingerprint,
            links.Count,
            links.Select(item => new { item.TargetProtocol, item.TargetBackendInstanceId, item.LibraryScopeId })
                .Distinct().Count(),
            rows.Length,
            rows.Count(item => item.Entry.RouteKind == "local"),
            rows.Count(item => item.Entry.RouteKind == "external" && IsExactProvider(item.Entry.RouteProviderId)),
            rows.Count(item => item.Entry.RouteKind == "external" && !IsExactProvider(item.Entry.RouteProviderId)),
            rows.Count(item => item.Entry.RouteKind == "unmatched"),
            rows.Count(item => overrides.ContainsKey(item.Entry.ExternalSnapshotId)),
            rows.Count(item => staleIds.Contains(item.Entry.ExternalSnapshotId)),
            rows.Count(item => conflictingIds.Contains(item.Entry.ExternalSnapshotId)),
            rows.Count(item => targetIds.Contains(item.Entry.ExternalSnapshotId)),
            targets.Count,
            targets);
    }

    private static PlaylistRematchPreview Empty()
    {
        var fingerprint = Hash("no-linked-playlists");
        return new(fingerprint, fingerprint, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
    }

    private static bool IsExactProvider(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId) && !GenericProviders.Contains(providerId);

    private static IQueryable<TrackMatchRecord> LatestDecisions(IQueryable<TrackMatchRecord> decisions)
    {
        var versions = decisions.GroupBy(item => item.ExternalSnapshotId).Select(group => new
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

    private static LocalTrackMatchCandidate ToCandidate(LibraryTrackRecord item)
    {
        IReadOnlyDictionary<string, string>? providers = null;
        try { providers = JsonSerializer.Deserialize<Dictionary<string, string>>(item.ProviderIdsJson); }
        catch (JsonException) { }
        return new(item.Id, item.TenantId, item.OwnerUserId, item.BackendInstanceId,
            item.LibraryScopeId, item.BackendItemId, item.CanonicalRecordingId, item.Title,
            item.Artist, item.Album, item.AlbumArtist,
            item.DurationMilliseconds is > 0 ? item.DurationMilliseconds : null,
            item.Isrc, item.MusicBrainzRecordingId, null, providers);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record PreviewRow(
        PlaylistLinkRecord Link,
        DurablePlaylistEntryProjection Entry);
}

public sealed record PlaylistRematchJobPayload(string ConfirmationId, string ScopeFingerprint);

public sealed class PlaylistRematchJobHandler(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    PlaylistRematchService rematches,
    ITrackMatchRepository trackMatches,
    IPlatformClock clock) : IDurableJobHandler
{
    public const string Type = "playlist.rematch";
    public string JobType => Type;

    public async Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        PlaylistRematchJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<PlaylistRematchJobPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload?.ConfirmationId is not { Length: 64 } ||
            payload.ScopeFingerprint is not { Length: 64 } ||
            !IsHex(payload.ConfirmationId) || !IsHex(payload.ScopeFingerprint) ||
            !context.Claim.TenantId.HasValue || !context.Claim.OwnerUserId.HasValue)
            return DurableJobCompletion.Failure("playlist_rematch_payload_invalid", "The rematch payload is invalid.");

        var preview = await rematches.PreviewAsync(
            context.Claim.TenantId.Value, context.Claim.OwnerUserId.Value, cancellationToken);
        if (!preview.ScopeFingerprint.Equals(payload.ScopeFingerprint, StringComparison.Ordinal))
            return DurableJobCompletion.Failure(
                "playlist_rematch_scope_changed", "A playlist or library changed. Review the rematch preview again.");
        if (context.Claim.AttemptNumber == 1 &&
            !preview.ConfirmationId.Equals(payload.ConfirmationId, StringComparison.Ordinal))
            return DurableJobCompletion.Failure(
                "playlist_rematch_preview_changed", "The match state changed. Review the rematch preview again.");
        if (!preview.CanApply) return DurableJobCompletion.Success();

        var runtime = await LoadRuntimeAsync(context, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var completed = 0;
        var failures = 0;
        foreach (var chunk in preview.Targets.Chunk(50))
        {
            var eligible = await EligibleTargetsAsync(
                context.Claim.TenantId.Value, context.Claim.OwnerUserId.Value, chunk, cancellationToken);
            var audits = new List<AuditEventRecord>(chunk.Length);
            foreach (var target in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!eligible.Contains(target.ExternalSnapshotId))
                {
                    audits.Add(Audit(context, target.ExternalSnapshotId, "skipped_revision_conflict", null));
                    completed++;
                    continue;
                }

                var execution = CreateExecution(context, runtime, target, cancellationToken);
                var result = await trackMatches.RematchSnapshotAsync(
                    execution,
                    target.ExternalSnapshotId,
                    context.Claim.CorrelationId,
                    target.PolicyVersion,
                    cancellationToken);
                audits.Add(Audit(
                    context,
                    target.ExternalSnapshotId,
                    result.Succeeded ? "rematched" : $"skipped_{result.Failure.ToString().ToLowerInvariant()}",
                    result.Succeeded ? result.DecisionVersion : null));
                if (!result.Succeeded) failures++;
                completed++;
            }
            await SaveAuditsAsync(audits, cancellationToken);
            await context.ReportProgressAsync(new(
                "playlist.rematch",
                $"Reviewed {completed} of {preview.Targets.Count} stale tracks.",
                completed,
                preview.Targets.Count,
                ThroughputPerSecond: completed / Math.Max(Stopwatch.GetElapsedTime(started).TotalSeconds, .001)),
                cancellationToken);
        }
        return failures == 0
            ? DurableJobCompletion.Success()
            : DurableJobCompletion.Failure(
                "playlist_rematch_rows_failed",
                $"{failures} track decisions could not be rematched. Review the operation details before retrying.");
    }

    private async Task<HashSet<Guid>> EligibleTargetsAsync(
        Guid tenantId,
        Guid ownerUserId,
        IReadOnlyCollection<PlaylistRematchTarget> targets,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var linkIds = targets.Select(item => item.PlaylistLinkId).Distinct().ToArray();
        var snapshotIds = targets.Select(item => item.ExternalSnapshotId).ToArray();
        var links = await db.PlaylistLinks.AsNoTracking()
            .Where(item => linkIds.Contains(item.Id) && item.TenantId == tenantId && item.OwnerUserId == ownerUserId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => snapshotIds.Contains(item.Id) && item.TenantId == tenantId && item.OwnerUserId == ownerUserId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var protectedIds = await db.ManualTrackOverrides.AsNoTracking()
            .Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId) &&
                           item.RevokedAt == null)
            .Select(item => item.ExternalSnapshotId)
            .ToHashSetAsync(cancellationToken);
        var versions = await db.TrackMatches.AsNoTracking()
            .Where(item => item.TenantId == tenantId && snapshotIds.Contains(item.ExternalSnapshotId))
            .GroupBy(item => item.ExternalSnapshotId)
            .ToDictionaryAsync(group => group.Key, group => group.Max(item => item.DecisionVersion), cancellationToken);
        return targets.Where(target =>
                links.GetValueOrDefault(target.PlaylistLinkId)?.Revision == target.PlaylistLinkRevision &&
                snapshots.GetValueOrDefault(target.ExternalSnapshotId)?.SnapshotVersion == target.SnapshotVersion &&
                !protectedIds.Contains(target.ExternalSnapshotId) &&
                versions.GetValueOrDefault(target.ExternalSnapshotId) == target.DecisionVersion)
            .Select(item => item.ExternalSnapshotId)
            .ToHashSet();
    }

    private async Task<RuntimeIdentity> LoadRuntimeAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tenantId = context.Claim.TenantId!.Value;
        var ownerUserId = context.Claim.OwnerUserId!.Value;
        var user = await db.Users.AsNoTracking().SingleAsync(item =>
            item.Id == ownerUserId && item.TenantId == tenantId, cancellationToken);
        var identities = await db.BackendIdentities.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.UserId == ownerUserId)
            .ToDictionaryAsync(
                item => $"{item.BackendType.ToLowerInvariant()}\n{item.BackendInstanceId}",
                item => item.PrincipalId,
                cancellationToken);
        return new(user.DisplayName, identities);
    }

    private ProtocolExecutionContext CreateExecution(
        DurableJobExecutionContext context,
        RuntimeIdentity runtime,
        PlaylistRematchTarget target,
        CancellationToken cancellationToken)
    {
        var tenantId = context.Claim.TenantId!.Value;
        var ownerUserId = context.Claim.OwnerUserId!.Value;
        var protocol = target.TargetProtocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic;
        var backendType = protocol.ToString().ToLowerInvariant();
        if (!runtime.PrincipalIds.TryGetValue($"{backendType}\n{target.BackendInstanceId}", out var principalId))
            throw new UnauthorizedAccessException("The target backend identity is unavailable.");
        return new(
            protocol,
            target.BackendInstanceId,
            principalId,
            new AllstarrPrincipal(tenantId, ownerUserId, backendType, target.BackendInstanceId,
                principalId, runtime.DisplayName, false),
            context.Claim.CorrelationId,
            clock.UtcNow.AddMinutes(2),
            cancellationToken,
            libraryScopeId: target.LibraryScopeId);
    }

    private AuditEventRecord Audit(
        DurableJobExecutionContext context,
        Guid snapshotId,
        string outcome,
        int? decisionVersion) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = context.Claim.TenantId,
            ActorUserId = context.Claim.OwnerUserId,
            Category = "playlist-rematch",
            Action = "track.review",
            Outcome = outcome,
            CorrelationId = context.Claim.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                row = Convert.ToHexString(SHA256.HashData(snapshotId.ToByteArray()).AsSpan(0, 12)).ToLowerInvariant(),
                decisionVersion
            }),
            CreatedAt = clock.UtcNow
        };

    private static bool IsHex(string value) =>
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
