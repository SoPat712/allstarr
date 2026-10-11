using System.Data;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
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
        var related = await db.CanonicalRecordings.Include(item => item.Identifiers).Where(item =>
            item.MergedIntoId == null && item.Id != recording.Id && item.Identifiers.Any(identifier =>
                identifier.Kind == RecordingIdentifierKinds.MusicBrainz && identifier.Value == mbid ||
                identifier.Kind == RecordingIdentifierKinds.Isrc && codes.Contains(identifier.Value)))
            .ToListAsync(cancellationToken);
        foreach (var item in related.Append(recording))
            EnsureSignalsCompatible(item, null, mbid);
        var now = _clock.UtcNow;
        foreach (var other in related)
            recording = await MergeRecordsAsync(db, recording, other, actor, correlationId, now, cancellationToken);
        var changed = AttachIdentifier(db, recording, RecordingIdentifierKinds.MusicBrainz, mbid, "musicbrainz-isrc", now);
        foreach (var code in codes)
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
}
