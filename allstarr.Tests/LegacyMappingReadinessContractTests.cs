namespace allstarr.Tests;

public sealed class LegacyMappingReadinessContractTests
{
    [Fact]
    public void ImportedMappings_ReportPlayableAndReviewCounts()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "MappingController.cs"));

        Assert.Contains("ExternalTrackPlaybackPolicy.CanUseForPlayback", controller, StringComparison.Ordinal);
        Assert.Contains("playableCount", controller, StringComparison.Ordinal);
        Assert.Contains("needsReviewCount", controller, StringComparison.Ordinal);
        Assert.Contains("status = playable ? \"ready\" : \"needs_review\"", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticPlaylistMatching_QueriesOnlyPlaybackCapableProviders()
    {
        var matcher = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Spotify", "SpotifyTrackMatchingService.cs"));
        var providers = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Common", "MultiProviderMetadataService.cs"));

        Assert.Contains("SearchPlayableSongsAsync(metadataService", matcher, StringComparison.Ordinal);
        Assert.Contains("FindPlayableSongByIsrcAsync", matcher, StringComparison.Ordinal);
        Assert.Contains("GetEnabledStreamingProviders()", providers, StringComparison.Ordinal);
        Assert.Contains("GetEnabledDownloadProviders()", providers, StringComparison.Ordinal);
        Assert.Contains("includeExtensions: false", providers, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
