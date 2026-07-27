namespace allstarr.Services.Common;

public sealed record ApplicationCacheTierUsage(
    string Tier,
    long EntryCount,
    long PayloadBytes,
    long? MaximumBytes,
    int? MaximumEntryBytes,
    bool Enabled,
    long Hits = 0,
    long Misses = 0,
    long Writes = 0,
    long Evictions = 0)
{
    public double HitRatio => Hits + Misses == 0
        ? 0
        : (double)Hits / (Hits + Misses);
}

public sealed record ApplicationCacheDiagnosticsSnapshot(
    ApplicationCacheTierUsage Database,
    ApplicationCacheTierUsage Hot,
    ApplicationCacheTierUsage Media,
    IReadOnlyList<ApplicationCacheCategoryDiagnostics> Categories,
    ApplicationCacheActivitySnapshot Activity,
    DateTimeOffset CapturedAt)
{
    public ExtensionStorageUsageSnapshot ExtensionStorage { get; init; } =
        new(0, 0, 0, 0);
    public ApplicationCacheArtworkLimits ArtworkLimits { get; init; } =
        new(0, MediaAssetResolver.MaximumDecodedPixels);
}

public sealed record ApplicationCacheArtworkLimits(
    int MaximumEntryBytes,
    int MaximumDecodedPixels);

public sealed record ExtensionStorageUsageSnapshot(
    int ActiveExtensions,
    int EntryCount,
    long PayloadBytes,
    long MaximumBytes);

public sealed record ApplicationCacheActivitySnapshot(
    long CoalescedRequests,
    long StaleServes,
    long UpstreamBytesAvoided);

public sealed class ApplicationCacheActivityMetrics
{
    private long _coalescedRequests;
    private long _staleServes;
    private long _upstreamBytesAvoided;

    public void RecordCoalesced() => Interlocked.Increment(ref _coalescedRequests);
    public void RecordStaleServe() => Interlocked.Increment(ref _staleServes);
    public void RecordUpstreamBytesAvoided(long bytes) =>
        Interlocked.Add(ref _upstreamBytesAvoided, Math.Max(0, bytes));

    public ApplicationCacheActivitySnapshot Snapshot() => new(
        Volatile.Read(ref _coalescedRequests),
        Volatile.Read(ref _staleServes),
        Volatile.Read(ref _upstreamBytesAvoided));
}

public sealed record ApplicationCacheCategoryUsage(
    long EntryCount,
    long PayloadBytes);

public sealed record DatabaseCacheMaintenancePreview(
    int ScannedEntries,
    bool ScanLimitReached,
    int ExpiredEntries,
    int UnknownOwnerEntries,
    int DisabledCategoryEntries,
    int SupersededEntries,
    int OverQuotaEntries,
    long ReclaimableBytes,
    DateTimeOffset CapturedAt);

public sealed record ApplicationCacheMaintenancePreview(
    DatabaseCacheMaintenancePreview Metadata,
    FileMediaCacheMaintenancePreview Media,
    int UnreferencedArtworkPayloads,
    long UnreferencedArtworkBytes,
    bool ArtworkReferenceScanLimitReached,
    DateTimeOffset CapturedAt);

public sealed record ArtworkPayloadReferenceSnapshot(
    IReadOnlySet<string> PayloadKeys,
    bool ScanLimitReached);

public sealed record ApplicationCacheCategoryDiagnostics(
    string Category,
    string Owner,
    string StorageTier,
    bool Enabled,
    long EntryCount,
    long PayloadBytes,
    long FreshSeconds,
    long StaleSeconds,
    long MaximumBytes,
    int MaximumEntries,
    string WarmingRule,
    string InvalidationTrigger)
{
    public static ApplicationCacheCategoryDiagnostics From(
        ApplicationCacheCategoryPolicy policy,
        bool enabled,
        ApplicationCacheCategoryUsage? usage = null) => new(
        policy.Category.ToString(),
        policy.Owner,
        policy.StorageTier.ToString(),
        enabled,
        usage?.EntryCount ?? 0,
        usage?.PayloadBytes ?? 0,
        Math.Max(0, (long)policy.FreshFor.TotalSeconds),
        Math.Max(0, (long)policy.StaleFor.TotalSeconds),
        policy.MaximumBytes,
        policy.MaximumEntries,
        policy.WarmingRule.ToString(),
        policy.InvalidationTrigger);
}
