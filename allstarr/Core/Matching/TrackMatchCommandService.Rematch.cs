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
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
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
                null, ConcurrentWriteRetries, cancellationToken),
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
                null,
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
        var key = (actor.UserId, actor.IsAdministrator, externalSnapshotId);
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
                    (Guid UserId, bool IsAdministrator, Guid SnapshotId),
                    Lazy<Task<TrackRematchCommandResult>>>(key, pending));
        }
    }

    private async Task<TrackRematchCommandResult> RematchSnapshotAsync(
        TrackMatchActor actor,
        Guid externalSnapshotId,
        string correlationId,
        string policyVersion,
        ProtocolExecutionContext? execution,
        Guid? excludedProviderIdentityId,
        int retriesRemaining,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await db.ExternalMetadataSnapshots.SingleOrDefaultAsync(
            item => item.Id == externalSnapshotId,
            cancellationToken);
        if (snapshot == null)
            return new(false, TrackMatchCommandFailure.NotFound, "Track snapshot was not found");
        if (!actor.IsAdministrator && snapshot.OwnerUserId != actor.UserId)
            return new(false, TrackMatchCommandFailure.Forbidden, "Track snapshot is outside your account");
        var catalogActor = CatalogActor(actor, snapshot);

        var source = snapshot.ProviderTrackIdentityId.HasValue
            ? await db.ProviderTrackIdentities.SingleOrDefaultAsync(
                item => item.Id == snapshot.ProviderTrackIdentityId.Value,
                cancellationToken)
            : null;
        source ??= await db.ProviderTrackIdentities
            .Where(item => item.ProviderId == snapshot.ProviderId &&
                           item.ResourceKind == ProviderResourceKind.Track &&
                           item.ExternalIdHash == snapshot.ExternalIdHash &&
                           (item.Scope == ProviderIdentityScope.Catalog ||
                            item.ProviderAccountId == snapshot.ProviderAccountId))
            .OrderByDescending(item => item.ProviderAccountId == snapshot.ProviderAccountId)
            .FirstOrDefaultAsync(cancellationToken);
        var manual = (await ManualTrackOverrides.ReadAsync(
            db, snapshot, actor.UserId, cancellationToken)).Effective;
        var latestDecision = await db.TrackMatches.AsNoTracking()
            .Where(item => item.ExternalSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.DecisionVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var latestVersion = latestDecision?.DecisionVersion ?? 0;
        if (TrackRematchAllService.RequiresAuthorityGuard(policyVersion) &&
            await HasManualAuthorityAsync(
                db,
                snapshot,
                source?.CanonicalRecordingId ?? latestDecision?.CanonicalRecordingId,
                cancellationToken))
            return new(
                false,
                TrackMatchCommandFailure.Conflict,
                "A manual match became authoritative while the full rematch was running");
        var candidateQuery = execution == null
            ? await AccessibleTracksAsync(db, actor, cancellationToken)
            : LibraryTrackAccess.Query(db, execution, await libraryAccess.ResolveAsync(execution, cancellationToken));
        var candidates = await candidateQuery.Where(item =>
                item.BackendInstanceId == snapshot.BackendInstanceId)
            .ToListAsync(cancellationToken);
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

            snapshot.OwnerUserId,
            snapshot.BackendInstanceId,

            snapshot.ProviderAccountId,
            2,
            snapshot.SnapshotVersion,
            candidates.Select(item => item.BackendLibraryId).ToHashSet(StringComparer.Ordinal));
        var rejectedOverride =
            manual?.Decision == ManualOverrideDecision.Reject &&
            manual.LibraryTrackId.HasValue &&
            manual.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion
                ? new ScopedTrackMatchOverride(

                    snapshot.OwnerUserId,

                    sourceTrack.ProviderId,
                    sourceTrack.ExternalId,
                    null,
                    new HashSet<Guid> { manual.LibraryTrackId.Value })
                : null;
        var effectiveEngine = await DecisionEngineAsync(cancellationToken);
        var decision = effectiveEngine.Decide(scope, sourceTrack, localCandidates, rejectedOverride);
        PlayableTrackMatch? playable = null;
        if (execution != null &&
            playableSearch != null &&
            manual?.Decision is not ManualOverrideDecision.Pin &&
            !(manual?.Decision == ManualOverrideDecision.Reject && !manual.LibraryTrackId.HasValue) &&
            decision.State != TrackMatchReviewState.Pinned &&
            !effectiveEngine.CanSkipProviderComparison(decision))
        {
            var cachedRoutes = source == null
                ? []
                : await db.ProviderTrackIdentities.AsNoTracking()
                    .Where(item =>
                        item.CanonicalRecordingId == source.CanonicalRecordingId &&
                        item.Id != source.Id &&
                        item.Id != excludedProviderIdentityId &&
                        item.ResourceKind == ProviderResourceKind.Track &&
                        item.VerificationMethod != "source-snapshot-hash" &&
                        (item.Verification == ProviderIdentityVerification.Verified ||
                         item.Verification == ProviderIdentityVerification.Pinned))
                    .ToArrayAsync(cancellationToken);
            playable = source == null
                ? null
                : await playableSearch.ReuseAsync(
                    execution, sourceTrack, scope, cachedRoutes,
                    localCandidates.Select(sourceTrack), rejectedOverride, cancellationToken);
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
        try
        {
            if (externalRoutable && selectedExternal != null)
            {
                if (source == null)
                {
                    var canonical = CreateProvisionalRecording(actor.UserId, clock.UtcNow, payload.Isrc);
                    db.CanonicalRecordings.Add(canonical);
                    source = await AddSourceSnapshotIdentityAsync(
                        db, catalogActor, snapshot, canonical.Id, latestVersion + 1,
                        clock.UtcNow, cancellationToken);
                }
                canonicalRecordingId = await LinkExternalIdentitiesAsync(
                    db,
                    catalogActor,
                    source,
                    selectedExternal,
                    playable!.RoutableExternalCandidates,
                    decision.State,
                    latestVersion + 1,
                    clock.UtcNow,
                    cancellationToken);
                if (!canonicalRecordingId.HasValue)
                    return new(false, TrackMatchCommandFailure.Conflict,
                        "The source recording has conflicting identity evidence; review the match before merging it.");
            }
        }
        catch (InvalidOperationException) when (retriesRemaining > 0)
        {
            return await RematchSnapshotAsync(
                actor,
                externalSnapshotId,
                correlationId,
                policyVersion,
                execution,
                excludedProviderIdentityId,
                retriesRemaining - 1,
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
            input, snapshot.OwnerUserId,
            correlationId, clock.UtcNow);
        if (TrackRematchAllService.RequiresAuthorityGuard(policyVersion) &&
            await HasManualAuthorityAsync(
                db, snapshot, canonicalRecordingId, cancellationToken))
            return new(
                false,
                TrackMatchCommandFailure.Conflict,
                "A manual match became authoritative while the full rematch was running");
        db.TrackMatches.Add(record);
        if (TrackRematchAllService.IsManagedPolicy(policyVersion))
            db.AuditEvents.Add(TrackRematchAllService.SuccessAudit(

                snapshot.OwnerUserId,
                snapshot.Id,
                correlationId,
                record.DecisionVersion,
                record.DecidedAt));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            var winner = await db.TrackMatches.AsNoTracking().SingleOrDefaultAsync(item =>
                item.OwnerUserId == snapshot.OwnerUserId &&
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
                    excludedProviderIdentityId,
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

    private static async Task<bool> HasManualAuthorityAsync(
        AllstarrDbContext db,
        ExternalMetadataSnapshotRecord snapshot,
        Guid? canonicalRecordingId,
        CancellationToken cancellationToken)
    {
        return await ManualTrackOverrides.ForSource(db, snapshot).AsNoTracking().AnyAsync(item =>
            item.RevokedAt == null && (item.OwnerUserId == snapshot.OwnerUserId || item.OwnerUserId == null),
            cancellationToken);
    }

    private static async Task<Guid?> LinkExternalIdentitiesAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
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
            db, actor, source, selected, source.CanonicalRecordingId, true, verificationMethod,
            decisionVersion, now, cancellationToken);
        if (!canonicalRecordingId.HasValue)
            return null;
        foreach (var alternate in routable.Where(song =>
                     !string.Equals(song.ExternalProvider, selected.ExternalProvider, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(song.ExternalId, selected.ExternalId, StringComparison.Ordinal)))
        {
            await LinkExternalIdentityAsync(
                db, actor, source, alternate, canonicalRecordingId.Value, false, verificationMethod,
                decisionVersion, now, cancellationToken);
        }
        return canonicalRecordingId;
    }

    private static async Task<Guid?> LinkExternalIdentityAsync(
        AllstarrDbContext db,
        ProviderActorContext actor,
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
            if (primary && !await TryRetargetProvisionalIdentityAsync(
                    db, source, canonicalRecordingId, now, cancellationToken))
                return null;
            if (identity.VerificationMethod == ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod ||
                verificationMethod == "automatic-match" &&
                identity.VerificationMethod == "automatic-suggestion")
            {
                identity.Verification = ProviderIdentityVerification.Verified;
                identity.VerificationMethod = verificationMethod;
                identity.DecisionVersion = decisionVersion;
                identity.VerifiedAt = now;
                identity.UpdatedAt = now;
                identity.Revision++;
            }
            return canonicalRecordingId;
        }

        identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),

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
        };
        db.ProviderTrackIdentities.Add(identity);
        return canonicalRecordingId;
    }
}
