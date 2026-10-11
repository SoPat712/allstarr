using System.Data;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
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
        if (first.Id == second.Id) return ToIdentity(first);
        var ordered = new[] { first, second }.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray();
        var winner = ordered[0];
        var loser = ordered[1];
        var musicBrainzIds = winner.Identifiers.Concat(loser.Identifiers)
            .Where(item => item.Kind == RecordingIdentifierKinds.MusicBrainz)
            .Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (musicBrainzIds.Length > 1)
            throw new InvalidOperationException("Recordings with conflicting MusicBrainz identities cannot be merged.");

        var now = _clock.UtcNow;
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
        await context.CanonicalRecordings.Where(item => item.MergedIntoId == loser.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.MergedIntoId, winner.Id)
                .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
        await context.ProviderTrackIdentities.Where(item => item.CanonicalRecordingId == loser.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CanonicalRecordingId, winner.Id)
                .SetProperty(item => item.UpdatedAt, now).SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
        await context.LibraryTracks.Where(item => item.CanonicalRecordingId == loser.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CanonicalRecordingId, winner.Id)
                .SetProperty(item => item.UpdatedAt, now).SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
        await context.TrackMatches.Where(item => item.CanonicalRecordingId == loser.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CanonicalRecordingId, winner.Id)
                .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
        await context.ListeningEvents.Where(item => item.CanonicalRecordingId == loser.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CanonicalRecordingId, winner.Id)
                .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
        await context.RecommendationCandidates.Where(item => item.CanonicalRecordingId == loser.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CanonicalRecordingId, winner.Id)
                .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
        AddAudit(context, actor, correlationId, "canonical-recording.merge", "merged",
            new { recordingId = winner.Id, mergedRecordingId = loser.Id });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToIdentity(winner);
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

    private static async Task<CanonicalRecordingRecord?> FindRecordingByPublicIdAsync(
        AllstarrDbContext context, ProviderActorContext actor, string publicId, CancellationToken cancellationToken)
    {
        var record = await context.CanonicalRecordings.SingleOrDefaultAsync(item => item.PublicId == publicId, cancellationToken);
        if (record != null) return await FindSurvivorAsync(context, record.Id, cancellationToken);
        if (Guid.TryParse(publicId, out var recordingId)) return await FindSurvivorAsync(context, recordingId, cancellationToken);
        var (external, provider, type, externalId) = LocalLibraryService.ParseExternalResource(publicId);
        if (!external || type != "song") return null;
        if (provider == "allstarr" && Guid.TryParse(externalId, out recordingId))
            return await FindSurvivorAsync(context, recordingId, cancellationToken);
        var hash = CanonicalCatalogKeys.Hash(externalId!);
        var userId = actor.EffectiveUserId;
        var links = await context.ProviderTrackIdentities.AsNoTracking().Where(item =>
            item.ProviderId == provider && item.ResourceKind == ProviderResourceKind.Track &&
            item.CatalogNamespace == DefaultCatalog && item.ExternalIdHash == hash && item.ExternalId == externalId &&
            item.Verification == ProviderIdentityVerification.Verified &&
            item.VerificationMethod != "automatic-suggestion" &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod &&
            (item.Scope == ProviderIdentityScope.Catalog || userId != null &&
                context.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId && account.Enabled &&
                    (account.OwnerUserId == null || account.OwnerUserId == userId))))
            .Select(item => item.CanonicalRecordingId).Distinct().ToArrayAsync(cancellationToken);
        // Ambiguous account/catalog aliases cannot establish a shared public identity.
        return links.Length == 1 ? await FindSurvivorAsync(context, links[0], cancellationToken) : null;
    }

    private static async Task<CanonicalRecordingRecord?> FindSurvivorAsync(
        AllstarrDbContext context, Guid recordingId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        while (visited.Add(recordingId))
        {
            var record = await context.CanonicalRecordings.Include(item => item.Identifiers)
                .SingleOrDefaultAsync(item => item.Id == recordingId, cancellationToken);
            if (record?.MergedIntoId == null) return record;
            recordingId = record.MergedIntoId.Value;
        }
        throw new InvalidOperationException("A recording merge contains a cycle.");
    }
}
