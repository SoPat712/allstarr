namespace allstarr.Tests;

public sealed class OperationalLogRegressionContractTests
{
    [Fact]
    public void LegacySourceProjection_DoesNotDowngradeAnExistingDecision()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Matching", "TrackMatchCommandService.cs"));

        Assert.Contains("PersistAutomatedTrackMatchCommand", source, StringComparison.Ordinal);
        Assert.Contains("TrackMatchRecord", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderFanOut_TreatsTimeoutsAsDegradationAndPropagatesCallerCancellation()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Common", "MultiProviderMetadataService.cs"));

        Assert.Contains(
            "catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("catch (TimeoutException)", source, StringComparison.Ordinal);
        Assert.Contains("SearchAllAsync timed out for provider", source, StringComparison.Ordinal);
        Assert.Contains("SearchAllAsync timed out for extension", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_DoesNotRegisterTheObsoleteRedisSnapshotWorker()
    {
        var source = File.ReadAllText(FindRepositoryFile("allstarr", "Program.cs"));

        Assert.DoesNotContain("RedisPersistenceService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<RedisCacheService>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<IRedisConnectionFactory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ParallelMetadataService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSessions_UsePostgreSqlWithoutProcessOrFileAuthority()
    {
        var service = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Admin", "AdminAuthSessionService.cs"));
        var context = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Storage", "AllstarrDbContext.cs"));

        Assert.Contains("EfAdminAuthSessionStore", service, StringComparison.Ordinal);
        Assert.Contains("AdminAuthSessions", context, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions.protected", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", service, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", service, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointUsage_UsesRetentionBoundedAuditEventsWithoutCsvFiles()
    {
        var helper = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "Helpers.cs"));
        var diagnostics = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "DiagnosticsController.cs"));
        var audit = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Operations", "EndpointUsageAudit.cs"));

        Assert.Contains("EndpointUsageAudit", helper, StringComparison.Ordinal);
        Assert.Contains("AuditEvents", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendAllText", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllLines", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoints.csv", helper + diagnostics + audit, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistTrackContext_UsesTheBoundedSharedCacheWithoutAPrivateCleanupLoop()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Subsonic", "PlaylistSyncService.cs"));

        Assert.Contains("IApplicationCache cache", source, StringComparison.Ordinal);
        Assert.Contains("BuildPlaylistTrackContextKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupExpiredCacheEntriesAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconstructableGenreAndLyricsCaches_DoNotCreateParallelFileStores()
    {
        var genre = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Common", "GenreEnrichmentService.cs"));
        var lyrics = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Lyrics", "LyricsPrefetchService.cs"));

        Assert.DoesNotContain("GenreDirectory", genre, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveToFileCacheAsync", genre, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveLyricsToFileAsync", lyrics, StringComparison.Ordinal);
        Assert.DoesNotContain("WarmCacheFromFilesAsync", lyrics, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(parts)}");
    }
}
