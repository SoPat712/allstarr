using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> LoadCrossLinksAsync(
        AllstarrDbContext db, IEnumerable<Guid> recordingIds, CancellationToken cancellationToken)
    {
        var ids = recordingIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        var links = await db.ProviderTrackIdentities.AsNoTracking().Where(item =>
                ids.Contains(item.CanonicalRecordingId) && item.Scope == ProviderIdentityScope.Catalog &&
                item.Verification == ProviderIdentityVerification.Verified && item.VerificationMethod == "odesli-link")
            .ToArrayAsync(cancellationToken);
        return links.GroupBy(item => item.CanonicalRecordingId).ToDictionary(group => group.Key,
            group => (IReadOnlyDictionary<string, string>)group.GroupBy(item => item.ProviderId)
                .Where(provider => provider.Select(item => item.ExternalId).Distinct().Count() == 1)
                .ToDictionary(provider => provider.Key, provider => provider.First().ExternalId));
    }
}
