using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
using allstarr.Services.Spotify;

namespace allstarr.Core.Matching;

public static class DurableProviderRouteSelector
{
    public static IReadOnlyList<DurableProviderRoute> Select(
        ProviderTrackIdentityRecord? source,
        IEnumerable<ProviderTrackIdentityRecord> identities,
        IReadOnlyList<string> providerPriority,
        ManualTrackOverrideRecord? manual = null,
        Guid? canonicalRecordingId = null)
    {
        var priority = providerPriority
            .Select((providerId, index) => (providerId: AppleMusicClient.NormalizeProviderId(providerId), index))
            .GroupBy(item => item.providerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(item => item.index), StringComparer.OrdinalIgnoreCase);
        var manualRoute = manual?.Decision == ManualOverrideDecision.Pin &&
                          !string.IsNullOrWhiteSpace(manual.TargetProviderId) &&
                          !string.IsNullOrWhiteSpace(manual.TargetExternalId) &&
                          priority.ContainsKey(AppleMusicClient.NormalizeProviderId(manual.TargetProviderId)) &&
                          ExternalTrackPlaybackPolicy.CanUseForPlayback(
                              manual.TargetProviderId, manual.TargetExternalId)
            ? new DurableProviderRoute(manual.TargetProviderId, manual.TargetExternalId, true)
            : null;
        if (source == null) return manualRoute == null ? [] : [manualRoute];
        var canonicalId = canonicalRecordingId ?? source.CanonicalRecordingId;
        var automatic = identities
            .Where(item =>
                item.CanonicalRecordingId == canonicalId &&
                item.ResourceKind == ProviderResourceKind.Track &&
                (item.Scope == ProviderIdentityScope.Catalog || item.Id == source.Id) &&
                item.VerificationMethod != "source-snapshot-hash" &&
                CatalogIdentityPolicy.IsActive(item) &&
                item.Verification is ProviderIdentityVerification.Verified or
                    ProviderIdentityVerification.Pinned &&
                priority.ContainsKey(AppleMusicClient.NormalizeProviderId(item.ProviderId)) &&
                ExternalTrackPlaybackPolicy.CanUseForPlayback(item.ProviderId, item.ExternalId))
            .OrderBy(item => priority.GetValueOrDefault(AppleMusicClient.NormalizeProviderId(item.ProviderId), int.MaxValue))
            .ThenByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
            .ThenByDescending(item => item.DecisionVersion)
            .ThenByDescending(item => item.VerifiedAt)
            .ThenBy(item => item.Id)
            .DistinctBy(item => $"{AppleMusicClient.NormalizeProviderId(item.ProviderId)}:{item.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(item => new DurableProviderRoute(
                item.ProviderId,
                item.ExternalId,
                false))
            .ToArray();
        return manualRoute == null
            ? automatic
            : [manualRoute, .. automatic.Where(item =>
                !AppleMusicClient.NormalizeProviderId(item.ProviderId).Equals(
                    AppleMusicClient.NormalizeProviderId(manualRoute.ProviderId), StringComparison.OrdinalIgnoreCase) ||
                !item.ExternalId.Equals(manualRoute.ExternalId, StringComparison.OrdinalIgnoreCase))];
    }
}
