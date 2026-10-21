using System.Data;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    public async Task<int> AddExactAliasesAsync(ProviderExecutionContext execution, ProviderExternalResourceId source,
        IReadOnlyList<ProviderExternalResourceId> aliases, string verificationMethod = "odesli-link",
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        ThrowIfUnavailable(execution, cancellationToken);
        source.RequireOwner(execution.ProviderId, ProviderResourceKind.Track);
        if (aliases.Count > 32) throw new ArgumentException("Too many provider aliases.");
        if (!await CanAutomaticallyLinkAsync(execution.Actor, source.ProviderId, cancellationToken)) return 0;
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var account = await ValidateExecutionContextAsync(db, execution, cancellationToken);
        var identity = await ResolveCoreAsync(db, source, account?.Id, cancellationToken);
        if (identity?.Verification != ProviderIdentityVerification.Verified ||
            identity.VerificationMethod is CatalogIdentityPolicy.SupersededVerificationMethod or "automatic-suggestion" or
                ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod)
            return 0;
        var added = 0;
        foreach (var alias in aliases.Distinct())
        {
            RequireTrack(alias);
            if (alias.ProviderId == source.ProviderId) continue;
            if (!await CanAutomaticallyLinkAsync(execution.Actor, alias.ProviderId, cancellationToken)) continue;
            // Existing associations, including manual choices and conflicting recordings, retain authority.
            if (await FindExactLinkAsync(db, ExactKey(alias), ProviderIdentityScope.Catalog, null, true, cancellationToken) != null)
                continue;
            AddLink(db, execution, new(identity.CanonicalRecordingId, alias, ProviderIdentityScope.Catalog,
                ProviderIdentityVerification.Verified, verificationMethod, 1), null, _clock.UtcNow);
            added++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    public async Task<CanonicalRecordingIdentity> EnrichRecordingAsync(ProviderActorContext actor, Guid recordingId,
        string expectedIsrc, string musicBrainzId, IReadOnlyCollection<string> isrcs, string correlationId,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        if (actor.EffectiveUserId == null) throw new UnauthorizedAccessException("Enrichment requires a user actor.");
        correlationId = ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100);
        var mbid = NormalizeMusicBrainzRecordingId(musicBrainzId) ?? throw new ArgumentException("A recording MBID is required.");
        var expected = NormalizeIsrc(expectedIsrc) ?? throw new ArgumentException("The lookup ISRC is required.");
        if (isrcs.Count is < 1 or > 100) throw new ArgumentException("The returned ISRC list is invalid.");
        var codes = isrcs.Select(value => NormalizeIsrc(value) ?? throw new ArgumentException("An ISRC is required."))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (!codes.Contains(expected)) throw new ArgumentException("The evidence does not include the lookup ISRC.");

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ValidateActorAsync(db, actor, cancellationToken);
        var recording = await FindSurvivorAsync(db, recordingId, cancellationToken)
            ?? throw new KeyNotFoundException("The recording does not exist.");
        if (!recording.Identifiers.Any(item => item.Kind == RecordingIdentifierKinds.Isrc && item.Value == expected))
            throw new InvalidOperationException("The recording no longer has the lookup ISRC.");
        if (!await HasAutomaticProviderIdentityAsync(db, recording.Id, actor, cancellationToken))
            return ToIdentity(recording);
        var related = await db.CanonicalRecordings.Include(item => item.Identifiers).Where(item =>
            item.MergedIntoId == null && item.Id != recording.Id && item.Identifiers.Any(identifier =>
                identifier.Kind == RecordingIdentifierKinds.MusicBrainz && identifier.Value == mbid ||
                identifier.Kind == RecordingIdentifierKinds.Isrc && codes.Contains(identifier.Value)))
            .ToListAsync(cancellationToken);
        var eligibleRelated = new List<CanonicalRecordingRecord>(related.Count);
        var restrictedIsrcs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in related)
        {
            if (await HasAutomaticProviderIdentityAsync(db, item.Id, actor, cancellationToken))
                eligibleRelated.Add(item);
            else
            {
                if (item.Identifiers.Any(identifier => identifier.Kind == RecordingIdentifierKinds.MusicBrainz && identifier.Value == mbid))
                    return ToIdentity(recording);
                restrictedIsrcs.UnionWith(item.Identifiers.Where(identifier => identifier.Kind == RecordingIdentifierKinds.Isrc)
                    .Select(identifier => identifier.Value));
            }
        }
        related = eligibleRelated;
        foreach (var item in related.Append(recording))
            EnsureSignalsCompatible(item, null, mbid);
        var now = _clock.UtcNow;
        foreach (var other in related)
            recording = await MergeRecordsAsync(db, recording, other, actor, correlationId, now, cancellationToken);
        var changed = AttachIdentifier(db, recording, RecordingIdentifierKinds.MusicBrainz, mbid, "musicbrainz-isrc", now);
        foreach (var code in codes.Where(code => !restrictedIsrcs.Contains(code)))
            changed |= AttachIdentifier(db, recording, RecordingIdentifierKinds.Isrc, code, "musicbrainz-isrc", now);
        if (changed)
        {
            recording.IsProvisional = false;
            recording.UpdatedAt = now;
            recording.Revision++;
            AddAudit(db, actor, correlationId, "canonical-recording.enrich", "enriched",
                new { canonicalRecordingId = recording.Id, identifierCount = recording.Identifiers.Count });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToIdentity(recording);
    }

    private async Task<bool> HasAutomaticProviderIdentityAsync(
        AllstarrDbContext db, Guid recordingId, ProviderActorContext actor,
        CancellationToken cancellationToken)
    {
        var providers = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.CanonicalRecordingId == recordingId &&
                           item.ResourceKind == ProviderResourceKind.Track)
            .Select(item => item.ProviderId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (providers.Length == 0) return true;
        foreach (var provider in providers)
        {
            if (await CanAutomaticallyLinkAsync(actor, provider, cancellationToken))
                return true;
        }
        return false;
    }
}
