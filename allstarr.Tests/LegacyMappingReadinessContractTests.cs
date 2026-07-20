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
        Assert.Contains("GetEnabledPlaybackProviders()", providers, StringComparison.Ordinal);
        Assert.Contains("requirePlayableExtensions: true", providers, StringComparison.Ordinal);
        Assert.Contains("playbackProviderRanks", matcher, StringComparison.Ordinal);
        Assert.Contains("fuzzy-local-library", matcher, StringComparison.Ordinal);
        Assert.Contains("candidate.MatchedSong.IsLocal", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("usedJellyfinIds", matcher, StringComparison.Ordinal);
        Assert.DoesNotContain("usedSongIds", matcher, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyPlaylistSources_AreProjectedIntoTheDurableIdentityGraph()
    {
        var program = File.ReadAllText(FindRepositoryFile("allstarr", "Program.cs"));
        var matcher = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Spotify", "SpotifyTrackMatchingService.cs"));
        var projector = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Spotify", "LegacySpotifyMappingProjector.cs"));

        Assert.Contains("AddSingleton<allstarr.Services.Spotify.LegacySpotifyMappingProjector>()", program, StringComparison.Ordinal);
        Assert.Contains("ProjectSourceTracksAsync(spotifyTracks", matcher, StringComparison.Ordinal);
        Assert.Contains("ProviderTrackIdentities", projector, StringComparison.Ordinal);
        Assert.Contains("LibraryTracks", projector, StringComparison.Ordinal);
        Assert.Contains("ProjectAllAsync", projector, StringComparison.Ordinal);
        Assert.Contains("ProjectSourceTracksAsync", projector, StringComparison.Ordinal);
        Assert.Contains("ProjectConfiguredSourceTracksAsync", projector, StringComparison.Ordinal);
        Assert.Contains("playlistFetcher.GetPlaylistTracksAsync", projector, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryIndexMaintenance_ContinuouslyBackfillsAudioTracks()
    {
        var program = File.ReadAllText(FindRepositoryFile("allstarr", "Program.cs"));
        var indexing = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Matching", "BackendLibraryIndexing.cs"));

        Assert.Contains("AddBackendLibraryIndexing()", program, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<LibraryIndexMaintenanceService>()", indexing, StringComparison.Ordinal);
        Assert.Contains("IncludeItemTypes=Audio", indexing, StringComparison.Ordinal);
        Assert.Contains("library.index", indexing, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResults_AreInterleavedWithinTheRequestedLimitAndPlayableExtensionsAreEligible()
    {
        var search = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Common", "MultiProviderMetadataService.cs"));

        Assert.Contains("InterleaveLists(allResultsList).Take(Math.Max(0, limit))", search, StringComparison.Ordinal);
        Assert.Contains("requirePlayableExtensions: true", search, StringComparison.Ordinal);
        Assert.Contains("extension.Types.Any(IsPlaybackCapability)", search, StringComparison.Ordinal);
        Assert.Contains("ConfiguredSearchOrder(requirePlayableExtensions)", search, StringComparison.Ordinal);
        Assert.Contains("ProviderSearchTimeout", search, StringComparison.Ordinal);
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
