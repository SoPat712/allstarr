using allstarr.Core.Storage;

namespace allstarr.Core.Matching;

internal static class CatalogIdentityPolicy
{
    public const string SupersededVerificationMethod = "superseded";

    public static bool IsActive(ProviderTrackIdentityRecord identity) =>
        identity.Verification is ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned &&
        identity.VerificationMethod is not (SupersededVerificationMethod or
            ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
            ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod);

    public static bool IsPlaybackEligible(ProviderTrackIdentityRecord identity) =>
        IsActive(identity) && identity.VerificationMethod != "automatic-suggestion";

    public static IOrderedEnumerable<ProviderTrackIdentityRecord> OrderForPlayback(
        IEnumerable<ProviderTrackIdentityRecord> identities) => identities
        .OrderByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
        .ThenByDescending(item => item.DecisionVersion)
        .ThenByDescending(item => item.VerifiedAt)
        .ThenBy(item => item.Id);

    public static void Supersede(ProviderTrackIdentityRecord identity, DateTimeOffset now)
    {
        if (identity.Verification == ProviderIdentityVerification.Pinned || !IsActive(identity)) return;
        identity.VerificationMethod = SupersededVerificationMethod;
        identity.UpdatedAt = now;
        identity.Revision++;
    }
}
