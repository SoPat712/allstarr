using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Spotify;

namespace allstarr.Core.Matching;

public static class DurableProviderRouteSelector
{
    public static IReadOnlyList<DurableProviderRoute> Select(
        ProviderTrackIdentityRecord? source,
        IEnumerable<ProviderTrackIdentityRecord> identities,
        IReadOnlyList<string> providerPriority)
    {
        if (source == null) return [];
        var priority = providerPriority
            .Select((providerId, index) => (providerId, index))
            .ToDictionary(item => item.providerId, item => item.index, StringComparer.Ordinal);
        return identities
            .Where(item =>
                item.TenantId == source.TenantId &&
                item.CanonicalRecordingId == source.CanonicalRecordingId &&
                item.ResourceKind == ProviderResourceKind.Track &&
                (item.Scope == ProviderIdentityScope.Catalog || item.Id == source.Id) &&
                item.Verification is ProviderIdentityVerification.Verified or
                    ProviderIdentityVerification.Pinned &&
                priority.ContainsKey(item.ProviderId) &&
                ExternalTrackPlaybackPolicy.CanUseForPlayback(item.ProviderId))
            .OrderBy(item => priority.GetValueOrDefault(item.ProviderId, int.MaxValue))
            .ThenByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
            .ThenByDescending(item => item.DecisionVersion)
            .ThenByDescending(item => item.VerifiedAt)
            .DistinctBy(item => $"{item.ProviderId}:{item.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(item => new DurableProviderRoute(
                item.ProviderId,
                item.ExternalId,
                item.Verification == ProviderIdentityVerification.Pinned))
            .ToArray();
    }
}
