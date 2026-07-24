namespace allstarr.Tests;

public sealed class BackgroundOperationInventoryContractTests
{
    [Fact]
    public void Inventory_CoversLegacyAndCanonicalOperationOwners()
    {
        var inventory = File.ReadAllText(FindRepositoryFile(
            "docs",
            "architecture",
            "background-operation-inventory.md"));

        string[] requiredOwners =
        [
            "DurableJobQueue",
            "DurableJobWorker",
            "DurableScheduleWorker",
            "ExtensionRuntimeCoordinator",
            "SpotifyPlaylistFetcher",
            "SpotifyMissingTracksFetcher",
            "SpotifyTrackMatchingService",
            "LegacyPlaylistMatchAllJobHandler",
            "PlaylistMaterializationJobHandler",
            "LibraryIndexMaintenanceService",
            "LibraryIndexJobHandler",
            "BackendLibraryRefreshJobHandler",
            "PlaybackSignalJobHandler",
            "FavoriteActionJobHandler",
            "RecommendationRunJobHandler",
            "GeneratedSetMaterializationJobHandler"
        ];

        foreach (var owner in requiredOwners)
        {
            Assert.Contains(owner, inventory, StringComparison.Ordinal);
        }

        Assert.Contains("Idempotency", inventory, StringComparison.Ordinal);
        Assert.Contains("Progress and retry", inventory, StringComparison.Ordinal);
        Assert.Contains("Migration order", inventory, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_CoversRegisteredLifecycleServices()
    {
        var inventory = File.ReadAllText(FindRepositoryFile(
            "docs",
            "architecture",
            "background-operation-inventory.md"));

        string[] lifecycleOwners =
        [
            "IdentityBootstrapper",
            "DurableStorageInitializer",
            "DurableStorageRuntimeMonitor",
            "DefaultTenantRuntimeSettingsProjector",
            "StartupValidationOrchestrator",
            "FirstPartyExtensionBootstrapper",
            "DurableProviderHealthInitializer",
            "ManagedProviderAccountHealthWarmupService",
            "ProviderCtsWarmupService",
            "CacheCleanupService",
            "LegacyMappingImportService",
            "AuditEventRetentionService",
            "PlatformTraceCollector",
            "SidecarHealthMonitor",
            "DurableOutboxDispatcher"
        ];

        foreach (var owner in lifecycleOwners)
        {
            Assert.Contains(owner, inventory, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(path)}");
    }
}
