using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Spotify;

namespace allstarr.Core.Matching;

public static class DurableProviderRouteSelector
{
    public static IReadOnlyList<DurableProviderRoute> Select(
        ProviderTrackIdentityRecord? source,
        IEnumerable<ProviderTrackIdentityRecord> identities,
        IReadOnlyList<string> providerPriority,
        ManualTrackOverrideRecord? manual = null)
    {
        var priority = providerPriority
            .Select((providerId, index) => (providerId, index))
            .ToDictionary(item => item.providerId, item => item.index, StringComparer.OrdinalIgnoreCase);
        var manualRoute = manual?.Decision == ManualOverrideDecision.Pin &&
                          !string.IsNullOrWhiteSpace(manual.TargetProviderId) &&
                          !string.IsNullOrWhiteSpace(manual.TargetExternalId) &&
                          priority.ContainsKey(manual.TargetProviderId) &&
                          ExternalTrackPlaybackPolicy.CanUseForPlayback(
                              manual.TargetProviderId, manual.TargetExternalId)
            ? new DurableProviderRoute(manual.TargetProviderId, manual.TargetExternalId, true)
            : null;
        if (source == null) return manualRoute == null ? [] : [manualRoute];
        var automatic = identities
            .Where(item =>
                item.TenantId == source.TenantId &&
                item.CanonicalRecordingId == source.CanonicalRecordingId &&
                item.ResourceKind == ProviderResourceKind.Track &&
                (item.Scope == ProviderIdentityScope.Catalog || item.Id == source.Id) &&
                item.VerificationMethod != "source-snapshot-hash" &&
                item.Verification is ProviderIdentityVerification.Verified or
                    ProviderIdentityVerification.Pinned &&
                priority.ContainsKey(item.ProviderId) &&
                ExternalTrackPlaybackPolicy.CanUseForPlayback(item.ProviderId, item.ExternalId))
            .OrderBy(item => priority.GetValueOrDefault(item.ProviderId, int.MaxValue))
            .ThenByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
            .ThenByDescending(item => item.DecisionVersion)
            .ThenByDescending(item => item.VerifiedAt)
            .DistinctBy(item => $"{item.ProviderId}:{item.ExternalId}", StringComparer.OrdinalIgnoreCase)
            .Select(item => new DurableProviderRoute(
                item.ProviderId,
                item.ExternalId,
                false))
            .ToArray();
        return manualRoute == null
            ? automatic
            : [manualRoute, .. automatic.Where(item =>
                !item.ProviderId.Equals(manualRoute.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                !item.ExternalId.Equals(manualRoute.ExternalId, StringComparison.OrdinalIgnoreCase))];
    }
}
