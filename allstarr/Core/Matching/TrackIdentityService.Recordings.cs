using System.Data;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed class RecordingMergeConflictException(string message) : InvalidOperationException(message);

public sealed partial class TrackIdentityService
{
    public async Task<Guid> RememberSourceAsync(ProviderExecutionContext execution,
        ProviderExternalResourceId externalId, string publicId, ProviderIdentityScope scope, string? isrc = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        ThrowIfUnavailable(execution, cancellationToken);
        publicId = ValidatePublicId(publicId);
        var normalizedIsrc = NormalizeIsrc(isrc);
        var request = new TrackIdentityLinkRequest(Guid.CreateVersion7(), externalId, scope,
            ProviderIdentityVerification.Verified, "source-use", 1);
        ValidateLinkRequest(execution, request);
        var userId = execution.Actor.EffectiveUserId ?? throw new UnauthorizedAccessException("A user actor is required.");
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var account = await ValidateExecutionContextAsync(db, execution, cancellationToken);
        var existing = await ResolveCoreAsync(db, externalId, account?.Id, cancellationToken);
        // A use never changes an existing manual or suggested association.
        if (existing != null) return existing.CanonicalRecordingId;
        var recording = await FindCanonicalByExactSignalsAsync(db, normalizedIsrc, null, cancellationToken);
        var previous = await FindRecordingByPublicIdAsync(db, execution.Actor, publicId, cancellationToken);
        if (recording != null && previous != null && recording.Id != previous.Id)
        {
            EnsureSourceSignalsCompatible(previous, normalizedIsrc);
            recording = await MergeRecordsAsync(db, recording, previous, execution.Actor,
                execution.CorrelationId, _clock.UtcNow, cancellationToken);
        }
        recording ??= previous;
        var created = recording == null;
        var now = _clock.UtcNow;
        if (recording == null)
        {
            recording = CreateProvisionalRecording(userId, now, publicId: publicId);
            db.CanonicalRecordings.Add(recording);
        }
        EnsureSourceSignalsCompatible(recording, normalizedIsrc);
        var enriched = AttachIdentifier(db, recording, RecordingIdentifierKinds.Isrc, normalizedIsrc, "source-use", now);
        if (!created && enriched)
        {
            recording.UpdatedAt = now;
            recording.Revision++;
        }
        if (created || enriched)
            AddAudit(db, execution.Actor, execution.CorrelationId,
                created ? "canonical-recording.create" : "canonical-recording.enrich",
                created ? "created" : "enriched", new { canonicalRecordingId = recording.Id, hasIsrc = normalizedIsrc != null });
        AddLink(db, execution, request with { CanonicalRecordingId = recording.Id },
            scope == ProviderIdentityScope.Account ? account!.Id : null, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return recording.Id;
    }

    // A provider can report a different ISRC for a track it served before. That leaves the
    // track unresolved for its caller instead of failing everything collected alongside it.
    private static void EnsureSourceSignalsCompatible(CanonicalRecordingRecord recording, string? isrc)
    {
        try
        {
            EnsureSignalsCompatible(recording, isrc, null);
        }
        catch (InvalidOperationException exception)
        {
            throw new RecordingMergeConflictException(exception.Message);
        }
    }

    public async Task<CanonicalRecordingIdentity?> ResolveRecordingAsync(
        ProviderActorContext actor, string publicId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureStorageReady();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateActorAsync(context, actor, cancellationToken);
        var recording = await FindRecordingByPublicIdAsync(context, actor, publicId, cancellationToken);
        return recording == null ? null : ToIdentity(recording);
    }

    public async Task<TrackIdentityResolution?> ResolveTargetAsync(
        ProviderExecutionContext executionContext, Guid recordingId, ProviderTrackIdentityTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(target);
        EnsureStorageReady();
        ThrowIfUnavailable(executionContext, cancellationToken);
        if (target.ProviderId != executionContext.ProviderId || target.ResourceKind != ProviderResourceKind.Track)
            throw new ArgumentException("The target must be a track owned by the execution provider.", nameof(target));
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ValidateExecutionContextAsync(context, executionContext, cancellationToken);
        var recording = await FindSurvivorAsync(context, recordingId, cancellationToken);
        if (recording == null) return null;
        var catalog = target.Catalog ?? DefaultCatalog;
        var candidates = await context.ProviderTrackIdentities.AsNoTracking().Where(item =>
                item.CanonicalRecordingId == recording.Id && item.ProviderId == target.ProviderId &&
                item.ResourceKind == ProviderResourceKind.Track && item.CatalogNamespace == catalog &&
                (item.Scope == ProviderIdentityScope.Catalog || account != null && item.ProviderAccountId == account.Id))
            .ToArrayAsync(cancellationToken);
        // A recording often has several verified releases on one provider (single, album, deluxe);
        // any of them plays the same recording, so pick the strongest rather than refusing the provider.
        var identity = PreferAccountScope(candidates, account?.Id)
            .Where(item => item.Verification is ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned &&
                item.VerificationMethod is not ("automatic-suggestion" or
                    ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
                    ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod))
            .OrderByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
            .ThenByDescending(item => item.DecisionVersion)
            .ThenByDescending(item => item.VerifiedAt)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (identity == null) return null;
        return ToResolution(identity) with { PublicId = recording.PublicId };
    }

    public async Task<CanonicalRecordingIdentity> MergeRecordingsAsync(
        ProviderActorContext actor, Guid firstId, Guid secondId, string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureStorageReady();
        if (actor.EffectiveUserId == null) throw new UnauthorizedAccessException("Merging recordings requires a user actor.");
        correlationId = ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ValidateActorAsync(context, actor, cancellationToken);
        var first = await FindSurvivorAsync(context, firstId, cancellationToken)
            ?? throw new KeyNotFoundException("The first recording does not exist.");
        var second = await FindSurvivorAsync(context, secondId, cancellationToken)
            ?? throw new KeyNotFoundException("The second recording does not exist.");
        var winner = await MergeRecordsAsync(context, first, second, actor, correlationId, _clock.UtcNow, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToIdentity(winner);
    }

    // The caller saves this unit of work, including any new source identity and match decision.
    internal static async Task<CanonicalRecordingRecord> MergeRecordsAsync(
        AllstarrDbContext context, CanonicalRecordingRecord first, CanonicalRecordingRecord second,
        ProviderActorContext actor, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (first.Id == second.Id) return first;
        var ordered = new[] { first, second }.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray();
        var winner = ordered[0];
        var loser = ordered[1];
        var musicBrainzIds = winner.Identifiers.Concat(loser.Identifiers)
            .Where(item => item.Kind == RecordingIdentifierKinds.MusicBrainz)
            .Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (musicBrainzIds.Length > 1)
            throw new RecordingMergeConflictException("Recordings with conflicting MusicBrainz identities cannot be merged.");

        await MergeLyricsChoicesAsync(context, winner.Id, loser.Id, now, cancellationToken);

        foreach (var identifier in loser.Identifiers.ToArray())
        {
            identifier.RecordingId = winner.Id;
            loser.Identifiers.Remove(identifier);
            winner.Identifiers.Add(identifier);
        }
        // Retain the losing public ID as a tombstone; never rewrite saved client IDs.
        loser.MergedIntoId = winner.Id;
        loser.UpdatedAt = now;
        loser.Revision++;
        winner.IsProvisional = Identifier(winner, RecordingIdentifierKinds.MusicBrainz) == null;
        winner.UpdatedAt = now;
        winner.Revision++;
        await context.CanonicalRecordings.Where(item => item.MergedIntoId == loser.Id).LoadAsync(cancellationToken);
        foreach (var record in context.CanonicalRecordings.Local.Where(item => item.MergedIntoId == loser.Id))
        {
            record.MergedIntoId = winner.Id;
            record.UpdatedAt = now;
            record.Revision++;
        }
        await context.ProviderTrackIdentities.Where(item => item.CanonicalRecordingId == loser.Id).LoadAsync(cancellationToken);
        foreach (var record in context.ProviderTrackIdentities.Local.Where(item => item.CanonicalRecordingId == loser.Id))
        {
            record.CanonicalRecordingId = winner.Id;
            record.UpdatedAt = now;
            record.Revision++;
        }
        await context.LibraryTracks.Where(item => item.CanonicalRecordingId == loser.Id).LoadAsync(cancellationToken);
        foreach (var record in context.LibraryTracks.Local.Where(item => item.CanonicalRecordingId == loser.Id))
        {
            record.CanonicalRecordingId = winner.Id;
            record.UpdatedAt = now;
            record.Revision++;
        }
        await context.TrackMatches.Where(item => item.CanonicalRecordingId == loser.Id).LoadAsync(cancellationToken);
        foreach (var record in context.TrackMatches.Local.Where(item => item.CanonicalRecordingId == loser.Id))
        {
            record.CanonicalRecordingId = winner.Id;
            record.Revision++;
        }
        await context.ListeningEvents.Where(item => item.CanonicalRecordingId == loser.Id).LoadAsync(cancellationToken);
        foreach (var record in context.ListeningEvents.Local.Where(item => item.CanonicalRecordingId == loser.Id))
        {
            record.CanonicalRecordingId = winner.Id;
            record.Revision++;
        }
        await context.RecommendationCandidates.Where(item => item.CanonicalRecordingId == loser.Id).LoadAsync(cancellationToken);
        foreach (var record in context.RecommendationCandidates.Local.Where(item => item.CanonicalRecordingId == loser.Id))
        {
            record.CanonicalRecordingId = winner.Id;
            record.Revision++;
        }
        AddAudit(context, actor, correlationId, "canonical-recording.merge", "merged",
            new { recordingId = winner.Id, mergedRecordingId = loser.Id });
        return winner;
    }

    private static async Task MergeLyricsChoicesAsync(AllstarrDbContext context, Guid winnerId, Guid loserId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var choices = await context.RecordingLyricsChoices.Where(item => item.RevokedAt == null &&
            (item.CanonicalRecordingId == winnerId || item.CanonicalRecordingId == loserId)).ToArrayAsync(cancellationToken);
        var losing = choices.Where(item => item.CanonicalRecordingId == loserId).ToArray();
        foreach (var choice in losing)
        {
            var existing = choices.FirstOrDefault(item => item.CanonicalRecordingId == winnerId && item.OwnerUserId == choice.OwnerUserId);
            if (existing != null && existing.SourceId != choice.SourceId)
                throw new RecordingMergeConflictException("Recordings with conflicting lyrics choices cannot be merged. Clear a choice before merging.");
        }
        foreach (var choice in losing)
        {
            if (choices.Any(item => item.CanonicalRecordingId == winnerId && item.OwnerUserId == choice.OwnerUserId))
                choice.RevokedAt = now;
            else choice.CanonicalRecordingId = winnerId;
            choice.UpdatedAt = now;
            choice.Revision++;
        }
    }

    internal static CanonicalRecordingRecord CreateProvisionalRecording(
        Guid userId, DateTimeOffset now, string? isrc = null, string? publicId = null)
    {
        var id = Guid.CreateVersion7();
        var recording = new CanonicalRecordingRecord
        {
            Id = id,
            CreatedByUserId = userId,
            PublicId = publicId == null ? CanonicalCatalogKeys.DefaultPublicId(id) : ValidatePublicId(publicId),
            IsProvisional = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (!string.IsNullOrWhiteSpace(isrc))
        {
            recording.Identifiers.Add(new RecordingIdentifierRecord
            {
                Id = Guid.CreateVersion7(),
                RecordingId = id,
                Kind = RecordingIdentifierKinds.Isrc,
                Value = isrc.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant(),
                Source = "match",
                CreatedAt = now
            });
        }
        return recording;
    }

    private static string ValidatePublicId(string publicId)
    {
        publicId = ProviderContractValidation.RequiredText(publicId, nameof(publicId), 200);
        var parsed = LocalLibraryService.ParseExternalResource(publicId);
        if (!parsed.isExternal || parsed.type != "song")
            throw new ArgumentException("A public recording ID must identify an external song.", nameof(publicId));
        return publicId;
    }

    private async Task<CanonicalRecordingRecord?> FindRecordingByPublicIdAsync(
        AllstarrDbContext context, ProviderActorContext actor, string publicId, CancellationToken cancellationToken)
    {
        if (publicId == null) return null;
        var recordings = await FindRecordingsByPublicIdsAsync(context, actor, [publicId], cancellationToken);
        return recordings[publicId];
    }

    private static async Task<CanonicalRecordingRecord?> FindSurvivorAsync(
        AllstarrDbContext context, Guid recordingId, CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, CanonicalRecordingRecord>? preloaded = null)
    {
        var visited = new HashSet<Guid>();
        while (visited.Add(recordingId))
        {
            var record = preloaded == null
                ? await context.CanonicalRecordings.Include(item => item.Identifiers)
                    .SingleOrDefaultAsync(item => item.Id == recordingId, cancellationToken)
                : preloaded.GetValueOrDefault(recordingId);
            if (record?.MergedIntoId == null) return record;
            recordingId = record.MergedIntoId.Value;
        }
        throw new InvalidOperationException("A recording merge contains a cycle.");
    }
}
