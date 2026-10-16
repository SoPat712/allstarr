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
        var existingSelection = latestDecision ?? await db.TrackMatches.AsNoTracking()
            .Where(item => item.OwnerUserId == snapshot.OwnerUserId &&
                db.ExternalMetadataSnapshots.Any(previous =>
                    previous.Id == item.ExternalSnapshotId &&
                    previous.OwnerUserId == snapshot.OwnerUserId &&
                    previous.Protocol == snapshot.Protocol &&
                    previous.BackendInstanceId == snapshot.BackendInstanceId &&
                    previous.BackendPrincipalId == snapshot.BackendPrincipalId &&
                    previous.ProviderId == snapshot.ProviderId &&
                    previous.ExternalIdHash == snapshot.ExternalIdHash))
            .OrderByDescending(item => item.DecidedAt)
            .ThenByDescending(item => item.DecisionVersion)
            .FirstOrDefaultAsync(cancellationToken);
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
        BackendLibraryAccessContext access;
        try
        {
            access = execution == null
                ? await libraryAccess.ResolveUserAsync(snapshot.OwnerUserId, cancellationToken)
                : new(execution, await libraryAccess.ResolveAsync(execution, cancellationToken));
        }
        catch (Exception exception) when (existingSelection?.LibraryTrackId.HasValue == true &&
            !cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            return await LocalLibraryUnavailableAsync(db, snapshot, correlationId, policyVersion, cancellationToken);
        }
        var candidateQuery = LibraryTrackAccess.Query(db, access);
        var candidates = await candidateQuery.Where(item =>
                item.BackendInstanceId == snapshot.BackendInstanceId)
            .ToListAsync(cancellationToken);
        if (existingSelection?.LibraryTrackId is { } existingLocalId &&
            !await LocalLibraryAvailableAsync(db, snapshot, existingLocalId, access, candidates, cancellationToken))
            return await LocalLibraryUnavailableAsync(db, snapshot, correlationId, policyVersion, cancellationToken);
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
            null)
        {
            ExactProviderAliases = source == null ? null :
                (await LoadCrossLinksAsync(db, [source.CanonicalRecordingId], cancellationToken))
                    .GetValueOrDefault(source.CanonicalRecordingId)
        };
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
                    var canonical = TrackIdentityService.CreateProvisionalRecording(actor.UserId, clock.UtcNow, payload.Isrc);
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

        if (crossLinks != null && source != null)
            await crossLinks.EnqueueAsync(snapshot.OwnerUserId, source.Id, correlationId, cancellationToken);
        return new(
            true,
            State: decision.State.ToString().ToLowerInvariant(),
            Confidence: decision.Confidence,
            CandidateCount: decision.Candidates.Count,
            DecisionVersion: record.DecisionVersion);
    }

    private async Task<TrackRematchCommandResult> LocalLibraryUnavailableAsync(
        AllstarrDbContext db,
        ExternalMetadataSnapshotRecord snapshot,
        string correlationId,
        string policyVersion,
        CancellationToken cancellationToken)
    {
        if (!TrackRematchAllService.IsManagedPolicy(policyVersion))
        {
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = snapshot.OwnerUserId,
                Category = "track-rematch",
                Action = "automatic.review",
                Outcome = "retry_local_library_unavailable",
                CorrelationId = correlationId,
                DetailsJson = JsonSerializer.Serialize(new { row = TrackRematchAllService.RowHash(snapshot.Id) }),
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(false, TrackMatchCommandFailure.LocalLibraryUnavailable,
            "The local library is temporarily unavailable. The existing local match was kept; retry after the library refresh succeeds.");
    }

    private async Task<bool> LocalLibraryAvailableAsync(
        AllstarrDbContext db,
        ExternalMetadataSnapshotRecord snapshot,
        Guid existingLocalId,
        BackendLibraryAccessContext access,
        IReadOnlyList<LibraryTrackRecord> candidates,
        CancellationToken cancellationToken)
    {
        if (configuration != null && snapshot.Protocol.Equals("jellyfin", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(configuration["Jellyfin:Url"]) || string.IsNullOrWhiteSpace(configuration["Jellyfin:ApiKey"])))
            return false;
        if (access.Context == null || !access.Access.Succeeded) return false;
        var existing = candidates.FirstOrDefault(item => item.Id == existingLocalId);
        if (existing == null) return false;
        var lastIndexedAt = candidates.Where(item =>
                item.BackendLibraryId == existing.BackendLibraryId)
            .Max(item => item.IndexedAt);
        if (clock.UtcNow - lastIndexedAt >= LocalLibraryAvailability.MaximumIndexAge) return false;
        var failures = await db.Jobs.AsNoTracking()
            .Where(item => item.Type == "library.index" && item.OwnerUserId == snapshot.OwnerUserId &&
                item.LastErrorCode != null && item.LastAttemptCompletedAt >= lastIndexedAt)
            .Select(item => item.PayloadJson)
            .ToArrayAsync(cancellationToken);
        foreach (var json in failures)
        {
            LibraryIndexJobPayload? payload;
            try { payload = JsonSerializer.Deserialize<LibraryIndexJobPayload>(json); }
            catch (JsonException) { continue; }
            if (payload?.BackendInstanceId == snapshot.BackendInstanceId &&
                payload.BackendPrincipalId == snapshot.BackendPrincipalId &&
                (payload.BackendLibraryId == null || payload.BackendLibraryId == existing.BackendLibraryId))
                return false;
        }
        return true;
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
        var providerId = allstarr.Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(
            song.ExternalProvider!);
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
            if (primary)
            {
                var mergedId = await TryMergeProvisionalIdentityAsync(
                    db, source, canonicalRecordingId, actor, "match-source-merge", now, cancellationToken);
                if (!mergedId.HasValue) return null;
                canonicalRecordingId = mergedId.Value;
            }
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
