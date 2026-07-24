namespace allstarr.Tests;

public sealed class PerformanceHotPathAuditContractTests
{
    [Fact]
    public void Audit_RecordsEveryRequiredRiskClassAndAcceptanceGate()
    {
        var audit = File.ReadAllText(FindRepositoryFile(
            "docs",
            "architecture",
            "performance-hot-path-audit.md"));

        string[] evidence =
        [
            "PlaylistController",
            "SpotifyMappingService.GetMappingAsync",
            "SpotifyTrackMatchingService",
            "SpotifyPlaylistFetcher.GetPlaylistTracksAsync",
            "MultiProviderMetadataService",
            "ExtensionManager",
            "BaseDownloadService",
            "JellyfinSessionManager",
            "PlaylistOrchestrationService"
        ];

        foreach (var item in evidence)
        {
            Assert.Contains(item, audit, StringComparison.Ordinal);
        }

        Assert.Contains("N+1", audit, StringComparison.Ordinal);
        Assert.Contains("Quadratic", audit, StringComparison.Ordinal);
        Assert.Contains("Duplicate network work", audit, StringComparison.Ordinal);
        Assert.Contains("Thread-pool starvation", audit, StringComparison.Ordinal);
        Assert.Contains("unbounded `Task.Run`", audit, StringComparison.Ordinal);
        Assert.Contains("Measurement gates", audit, StringComparison.Ordinal);
        Assert.Contains("Acceptance criteria", audit, StringComparison.Ordinal);
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
