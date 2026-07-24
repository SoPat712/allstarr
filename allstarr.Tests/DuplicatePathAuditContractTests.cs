namespace allstarr.Tests;

public sealed class DuplicatePathAuditContractTests
{
    [Fact]
    public void Audit_AssignsCanonicalOwnersAndRemovalPrerequisites()
    {
        var audit = File.ReadAllText(FindRepositoryFile(
            "docs",
            "architecture",
            "duplicate-path-audit.md"));

        string[] requiredPaths =
        [
            "JellyfinController.Spotify",
            "PlaylistVirtualizationService",
            "PlaybackTrackResolver",
            "PlaybackSignalPipeline",
            "SpotifyTrackMatchingService",
            "TrackMatchDecisionEngine",
            "SpotifyMappingService",
            "TrackMatchPersistenceService",
            "PlaylistTrackStatusResolver",
            "CacheWarmingService",
            "CacheCleanupService"
        ];

        foreach (var path in requiredPaths)
        {
            Assert.Contains(path, audit, StringComparison.Ordinal);
        }

        Assert.Contains("Canonical owner", audit, StringComparison.Ordinal);
        Assert.Contains("Removal prerequisite", audit, StringComparison.Ordinal);
        Assert.Contains("Removal order", audit, StringComparison.Ordinal);
        Assert.Contains("Redis unavailable", audit, StringComparison.Ordinal);
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
