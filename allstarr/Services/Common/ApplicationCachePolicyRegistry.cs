using allstarr.Models.Settings;

namespace allstarr.Services.Common;

public enum ApplicationCacheCategory
{
    SearchResults,
    PlaylistDiscovery,
    DerivedProjection,
    CanonicalMetadata,
    ProviderResponse,
    Artwork,
    Lyrics,
    TemporaryAudio,
    NegativeResult,
    Coordination
}

public enum ApplicationCacheStorageTier
{
    Metadata,
    Media
}

public enum ApplicationCacheWarmingRule
{
    None,
    VisibleOrSelected,
    OnDemand
}

public sealed record ApplicationCacheCategoryPolicy(
    ApplicationCacheCategory Category,
    string Owner,
    ApplicationCacheStorageTier StorageTier,
    TimeSpan FreshFor,
    TimeSpan StaleFor,
    long MaximumBytes,
    int MaximumEntries,
    ApplicationCacheWarmingRule WarmingRule,
    string InvalidationTrigger);

/// <summary>
/// Canonical ownership and retention policy for every reconstructable cache entry.
/// Durable mappings, event facts, credentials, and session state do not belong here.
/// </summary>
public static class ApplicationCachePolicyRegistry
{
    private const long Megabyte = 1024L * 1024L;

    public static ApplicationCacheCategory Classify(string key)
    {
        return TryClassify(key, out var category)
            ? category
            : ApplicationCacheCategory.ProviderResponse;
    }

    public static bool TryClassify(string key, out ApplicationCacheCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        category = default;

        if (CacheKeyBuilder.IsMediaAssetPayloadKey(key))
            category = ApplicationCacheCategory.Artwork;
        if (StartsWithAny(key, "lyrics:v2:", "lyrics:id:v2:"))
            category = ApplicationCacheCategory.Lyrics;
        if (StartsWithAny(
                key,
                "negative:playback:metadata:v1:",
                "negative:playback:route:v1:",
                "negative:musicbrainz:"))
            category = ApplicationCacheCategory.NegativeResult;
        if (key.StartsWith("playback:signal:dedupe:v1:", StringComparison.Ordinal))
            category = ApplicationCacheCategory.Coordination;
        if (StartsWithAny(
                key,
                "media:descriptor:v3:",
                "playlist:artwork-descriptor:v1:",
                "metadata:album:v1:",
                "metadata:artist:v1:",
                "musicbrainz:isrc:v2:",
                "musicbrainz:search:v2:",
                "musicbrainz:mbid:v2:",
                "genre:v2:",
                "playback:metadata:v1:"))
            category = ApplicationCacheCategory.CanonicalMetadata;
        if (key.StartsWith("search:v2:", StringComparison.Ordinal))
            category = ApplicationCacheCategory.SearchResults;
        if (key.StartsWith("playlist:discovery:v2:", StringComparison.Ordinal))
            category = ApplicationCacheCategory.PlaylistDiscovery;
        if (StartsWithAny(
                key,
                "odesli:tidal-to-spotify:v2:",
                "odesli:url-to-spotify:v2:",
                "odesli:translate:v2:",
                "jellyfin:item-type:v1:",
                "jellyfin:item-type:v2:"))
            category = ApplicationCacheCategory.ProviderResponse;

        return category != default ||
               key.StartsWith("search:v2:", StringComparison.Ordinal);
    }

    public static ApplicationCacheCategoryPolicy Resolve(string key, CacheSettings? settings = null) =>
        Resolve(Classify(key), settings);

    public static IReadOnlyList<ApplicationCacheCategoryPolicy> All(CacheSettings? settings = null) =>
        Enum.GetValues<ApplicationCacheCategory>()
            .Select(category => Resolve(category, settings))
            .ToArray();

    public static bool IsEnabled(string key, CacheSettings? settings = null) =>
        IsEnabled(Classify(key), settings);

    public static bool IsEnabled(
        ApplicationCacheCategory category,
        CacheSettings? settings = null)
    {
        settings ??= new CacheSettings();
        return !settings.CategoryEnabled.TryGetValue(category.ToString(), out var enabled) || enabled;
    }

    public static ApplicationCacheCategoryPolicy Resolve(
        ApplicationCacheCategory category,
        CacheSettings? settings = null)
    {
        settings ??= new CacheSettings();
        ApplicationCacheCategoryPolicy policy = category switch
        {
            ApplicationCacheCategory.SearchResults => new(
                category, "provider-search", ApplicationCacheStorageTier.Metadata,
                settings.SearchResultsTTL, TimeSpan.Zero, 16 * Megabyte, 10_000,
                ApplicationCacheWarmingRule.None, "query-or-account-revision"),
            ApplicationCacheCategory.PlaylistDiscovery => new(
                category, "playlist-discovery", ApplicationCacheStorageTier.Metadata,
                TimeSpan.FromMinutes(5), TimeSpan.Zero, 16 * Megabyte, 10_000,
                ApplicationCacheWarmingRule.VisibleOrSelected, "provider-account-or-source-revision"),
            ApplicationCacheCategory.DerivedProjection => new(
                category, "admin-read-model", ApplicationCacheStorageTier.Metadata,
                TimeSpan.FromMinutes(5), TimeSpan.Zero, 16 * Megabyte, 10_000,
                ApplicationCacheWarmingRule.VisibleOrSelected, "postgres-revision"),
            ApplicationCacheCategory.CanonicalMetadata => new(
                category, "canonical-media", ApplicationCacheStorageTier.Metadata,
                settings.MetadataTTL, TimeSpan.FromHours(12), 96 * Megabyte, 100_000,
                ApplicationCacheWarmingRule.VisibleOrSelected, "provider-or-library-revision"),
            ApplicationCacheCategory.ProviderResponse => new(
                category, "provider-gateway", ApplicationCacheStorageTier.Metadata,
                settings.MetadataTTL, TimeSpan.FromMinutes(30), 64 * Megabyte, 50_000,
                ApplicationCacheWarmingRule.None, "provider-account-or-storefront-revision"),
            ApplicationCacheCategory.Artwork => new(
                category, "media-assets", ApplicationCacheStorageTier.Media,
                settings.ProxyImagesTTL, TimeSpan.FromDays(1),
                Math.Max(Megabyte, settings.MediaMaximumMegabytes * Megabyte),
                Math.Max(1, settings.MediaCleanupFileLimit),
                ApplicationCacheWarmingRule.VisibleOrSelected, "resource-or-artwork-revision"),
            ApplicationCacheCategory.Lyrics => new(
                category, "lyrics-routing", ApplicationCacheStorageTier.Metadata,
                settings.LyricsTTL, TimeSpan.FromDays(1), 96 * Megabyte, 100_000,
                ApplicationCacheWarmingRule.OnDemand, "provider-or-track-revision"),
            ApplicationCacheCategory.TemporaryAudio => new(
                category, "playback-delivery", ApplicationCacheStorageTier.Media,
                settings.TranscodeCacheTTL, TimeSpan.Zero, 128 * Megabyte, 512,
                ApplicationCacheWarmingRule.None, "playback-complete-or-expiry"),
            ApplicationCacheCategory.NegativeResult => new(
                category, "provider-gateway", ApplicationCacheStorageTier.Metadata,
                TimeSpan.FromMinutes(2), TimeSpan.Zero, 8 * Megabyte, 10_000,
                ApplicationCacheWarmingRule.None, "provider-account-or-query-change"),
            ApplicationCacheCategory.Coordination => new(
                category, "runtime-coordination", ApplicationCacheStorageTier.Metadata,
                TimeSpan.FromMinutes(5), TimeSpan.Zero, 8 * Megabyte, 20_000,
                ApplicationCacheWarmingRule.None, "operation-complete-or-expiry"),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
        var categoryKey = category.ToString();
        var maximumEntries = settings.CategoryMaximumEntries.TryGetValue(categoryKey, out var configuredEntries)
            ? Math.Clamp(configuredEntries, 1, 1_000_000)
            : policy.MaximumEntries;
        var maximumBytes = settings.CategoryMaximumMegabytes.TryGetValue(categoryKey, out var configuredMegabytes)
            ? Math.Clamp(configuredMegabytes, 1, 1024 * 1024) * Megabyte
            : policy.MaximumBytes;
        return policy with
        {
            MaximumEntries = maximumEntries,
            MaximumBytes = maximumBytes
        };
    }

    private static bool StartsWithAny(string key, params string[] prefixes) =>
        prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
