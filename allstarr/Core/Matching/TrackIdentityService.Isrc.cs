using System.Data;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    // A song first remembered without its ISRC learns it on a later use, so the next service copy carrying that ISRC
    // joins this recording instead of starting another. A recording that already has an ISRC, or an ISRC another
    // recording already carries, is left as it is; joining those is a merge, not a use.
    private async Task LearnSourceIsrcAsync(AllstarrDbContext db, TrackIdentityResolution existing, string? isrc,
        ProviderExecutionContext execution, CancellationToken cancellationToken)
    {
        if (isrc == null || existing.Verification != ProviderIdentityVerification.Verified ||
            existing.VerificationMethod is "automatic-suggestion" or ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
                ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod or CatalogIdentityPolicy.SupersededVerificationMethod) return;
        var recording = await FindSurvivorAsync(db, existing.CanonicalRecordingId, cancellationToken);
        if (recording == null || Identifier(recording, RecordingIdentifierKinds.Isrc) != null ||
            await db.RecordingIdentifiers.AnyAsync(item => item.Kind == RecordingIdentifierKinds.Isrc && item.Value == isrc,
                cancellationToken)) return;
        var now = _clock.UtcNow;
        AttachIdentifier(db, recording, RecordingIdentifierKinds.Isrc, isrc, "source-use", now);
        recording.UpdatedAt = now;
        recording.Revision++;
        AddAudit(db, execution.Actor, execution.CorrelationId, "canonical-recording.enrich", "enriched",
            new { canonicalRecordingId = recording.Id, hasIsrc = true });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CanonicalRecordingIdentity> MergeRecordingsByIsrcAsync(ProviderActorContext actor, string isrc,
        Guid keepId, IReadOnlyCollection<Guid> mergedIds, string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mergedIds);
        EnsureStorageReady();
        if (actor.EffectiveUserId == null) throw new UnauthorizedAccessException("Merging recordings requires a user actor.");
        correlationId = ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100);
        var code = NormalizeIsrc(isrc) ?? throw new ArgumentException("An ISRC is required.", nameof(isrc));
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ValidateActorAsync(context, actor, cancellationToken);
        var keep = await FindSurvivorAsync(context, keepId, cancellationToken)
            ?? throw new KeyNotFoundException("The kept recording does not exist.");
        var now = _clock.UtcNow;
        foreach (var id in mergedIds.Distinct())
        {
            var other = await FindSurvivorAsync(context, id, cancellationToken)
                ?? throw new KeyNotFoundException("A merged recording does not exist.");
            keep = await MergeRecordsAsync(context, keep, other, actor, correlationId, now, cancellationToken, keepFirst: true);
        }
        // Identifiers of merged recordings now sit on the kept one, so a stored row for this ISRC elsewhere is another recording's.
        if (!keep.Identifiers.Any(item => item.Kind == RecordingIdentifierKinds.Isrc && item.Value == code) &&
            !await context.RecordingIdentifiers.AnyAsync(item => item.Kind == RecordingIdentifierKinds.Isrc && item.Value == code,
                cancellationToken) &&
            AttachIdentifier(context, keep, RecordingIdentifierKinds.Isrc, code, "isrc-merge", now))
        {
            keep.UpdatedAt = now;
            keep.Revision++;
        }
        var renames = PendingRenames(context);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PublishRenames(renames);
        return ToIdentity(keep);
    }
}
