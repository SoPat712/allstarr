using allstarr.Core.Storage;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
    private static async Task<IReadOnlyDictionary<string, string[]>> LoadMusicBrainzIsrcsAsync(
        AllstarrDbContext db, IEnumerable<string?> recordingIds, CancellationToken cancellationToken)
    {
        var ids = recordingIds.Where(value => Guid.TryParse(value, out _))
            .Select(value => Guid.Parse(value!).ToString("D")).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<string, string[]>();
        var identifiers = await (from recording in db.RecordingIdentifiers.AsNoTracking()
                                 join isrc in db.RecordingIdentifiers.AsNoTracking() on recording.RecordingId equals isrc.RecordingId
                                 where recording.Kind == RecordingIdentifierKinds.MusicBrainz && ids.Contains(recording.Value) &&
                                       isrc.Kind == RecordingIdentifierKinds.Isrc
                                 select new { recording.Value, Isrc = isrc.Value }).ToArrayAsync(cancellationToken);
        return identifiers.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Isrc).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string>? FilterAutomaticCrossLinks(
        IReadOnlyDictionary<string, string>? links,
        EffectiveProviderPolicySnapshot? policy) => links == null || policy == null
        ? links
        : links.Where(item => policy.IsAutomatic(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> LoadCrossLinksAsync(
        AllstarrDbContext db, IEnumerable<Guid> recordingIds, CancellationToken cancellationToken)
    {
        var ids = recordingIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        var links = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                ids.Contains(item.CanonicalRecordingId) && item.Scope == ProviderIdentityScope.Catalog &&
                item.Verification == ProviderIdentityVerification.Verified && (item.VerificationMethod == "odesli-link" || item.VerificationMethod == "isrc-lookup" ||
                 item.VerificationMethod == "cross-service-match"))
            .ToArrayAsync(cancellationToken);
        return links.GroupBy(item => item.CanonicalRecordingId).ToDictionary(group => group.Key,
            group => (IReadOnlyDictionary<string, string>)group.GroupBy(item => item.ProviderId)
                .Where(provider => provider.Select(item => item.ExternalId).Distinct().Count() == 1)
                .ToDictionary(provider => provider.Key, provider => provider.First().ExternalId));
    }
}
