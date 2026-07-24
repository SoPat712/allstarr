namespace allstarr.Services.Common;

public static class ApplicationCachePayloadPolicy
{
    public static bool IsDatabaseEligible(string key) =>
        ApplicationCachePolicyRegistry.Resolve(key).StorageTier ==
        ApplicationCacheStorageTier.Metadata;
}
