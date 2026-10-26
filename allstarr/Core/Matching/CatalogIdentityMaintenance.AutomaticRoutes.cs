using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class CatalogIdentityMaintenance
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { PropertyNameCaseInsensitive = true };

    private async Task RepairAutomaticRoutesAsync(CancellationToken cancellationToken)
    {
        Guid[] ids;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            ids = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                    item.Scope == ProviderIdentityScope.Catalog && item.ResourceKind == ProviderResourceKind.Track &&
                    item.Verification == ProviderIdentityVerification.Verified && item.VerificationMethod == "automatic-match")
                .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(cancellationToken);
        foreach (var batch in ids.Chunk(100))
        {
            await RepairAutomaticRouteBatchAsync(batch, cancellationToken);
            await Task.Yield();
        }
    }

    private async Task RepairAutomaticRouteBatchAsync(Guid[] ids, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var identities = await db.ProviderTrackIdentities.Where(item => ids.Contains(item.Id)).ToArrayAsync(cancellationToken);
        foreach (var identity in identities.Where(item => item.Verification == ProviderIdentityVerification.Verified &&
                     item.VerificationMethod == "automatic-match"))
        {
            var decisions = await db.TrackMatches.AsNoTracking().Where(item =>
                    item.CanonicalRecordingId == identity.CanonicalRecordingId &&
                    item.DecisionVersion == identity.DecisionVersion && item.State == TrackMatchState.Accepted)
                .OrderByDescending(item => item.DecidedAt).Take(33).ToArrayAsync(cancellationToken);
            if (decisions.Length > 32) continue;
            var evidence = decisions.Select(item => (Decision: item, Scores: ReadScores(item.CandidateResultsJson)))
                .Select(item => (item.Decision, item.Scores, Candidate: item.Scores.FirstOrDefault(score => Matches(score, identity))))
                .Where(item => item.Candidate != null && !string.IsNullOrWhiteSpace(item.Candidate.Title) &&
                    !string.IsNullOrWhiteSpace(item.Candidate.Artist)).ToArray();
            // Missing provenance is unknown; only a recorded failed acceptance can disqualify an old automatic route.
            if (evidence.Length == 0 || evidence.Any(item =>
                    TrackMatchDecisionEngine.IsAcceptedExternalCandidate(item.Candidate!, item.Decision.Threshold))) continue;
            var manual = await db.ManualTrackOverrides.AsNoTracking().Where(item =>
                    item.RevokedAt == null && item.Decision == ManualOverrideDecision.Pin &&
                    item.TargetExternalId == identity.ExternalId).Select(item => item.TargetProviderId)
                .ToArrayAsync(cancellationToken);
            if (manual.Any(provider => Normalize(provider!) == Normalize(identity.ProviderId))) continue;

            var siblings = await db.ProviderTrackIdentities.Where(item =>
                    item.CanonicalRecordingId == identity.CanonicalRecordingId && item.ProviderId == identity.ProviderId &&
                    item.CatalogNamespace == identity.CatalogNamespace && item.Scope == identity.Scope &&
                    item.ProviderAccountId == identity.ProviderAccountId && item.ResourceKind == ProviderResourceKind.Track)
                .ToArrayAsync(cancellationToken);
            var replacement = evidence.SelectMany(item => item.Scores
                    .Where(score => TrackMatchDecisionEngine.IsAcceptedExternalCandidate(score, item.Decision.Threshold))
                    .Select(score => (Score: score, item.Decision)))
                .OrderByDescending(item => item.Score.Confidence)
                .ThenBy(item => item.Score.BackendItemId, StringComparer.Ordinal)
                .Select(item => (Identity: siblings.FirstOrDefault(sibling =>
                    sibling.Verification == ProviderIdentityVerification.Verified &&
                    sibling.VerificationMethod == CatalogIdentityPolicy.SupersededVerificationMethod && Matches(item.Score, sibling)),
                    item.Decision.DecisionVersion))
                .FirstOrDefault(item => item.Identity != null);
            CatalogIdentityPolicy.Supersede(identity, clock.UtcNow);
            identity.VerificationMethod = "automatic-suggestion";
            if (replacement.Identity is { } restored && !siblings.Any(CatalogIdentityPolicy.IsPlaybackEligible))
            {
                restored.VerificationMethod = "automatic-match";
                restored.DecisionVersion = replacement.DecisionVersion;
                restored.VerifiedAt = clock.UtcNow;
                restored.UpdatedAt = clock.UtcNow;
                restored.Revision++;
            }
            var recording = await db.CanonicalRecordings.SingleAsync(item => item.Id == identity.CanonicalRecordingId, cancellationToken);
            recording.Revision++;
            recording.UpdatedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static TrackMatchCandidateScore[] ReadScores(string json)
    {
        try { return (JsonSerializer.Deserialize<TrackMatchCandidateScore[]>(json, EvidenceJson) ?? []).Where(item => item != null).ToArray(); }
        catch (JsonException) { return []; }
    }

    private static bool Matches(TrackMatchCandidateScore score, ProviderTrackIdentityRecord identity) =>
        score.ProviderTrackIds?.Any(item => Normalize(item.Key) == Normalize(identity.ProviderId) &&
            string.Equals(item.Value, identity.ExternalId, StringComparison.Ordinal)) == true;

    private static string Normalize(string provider) => AppleMusicClient.NormalizeProviderId(provider);
}
