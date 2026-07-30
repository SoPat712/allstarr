namespace allstarr.Tests;

public sealed class LegacyMappingReadinessContractTests
{
    [Fact]
    public void ImportedMappings_ReportPlayableAndReviewCounts()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "TrackMatchesController.cs"));

        Assert.Contains("ITrackMatchRepository", controller, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackMatchRequest", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingAndDownloadArtwork_UseProtectedScopedUrls()
    {
        var mappings = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "TrackMatchesController.cs"));
        var downloads = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "DownloadActivityController.cs"));

        Assert.Contains("sourceArtworkUrl", mappings, StringComparison.Ordinal);
        Assert.Contains("candidateArtworkUrl", mappings, StringComparison.Ordinal);
        Assert.DoesNotContain("artworkUrl = song.CoverArtUrl", mappings, StringComparison.Ordinal);
        Assert.Contains("new MediaAssetIdentity(", downloads, StringComparison.Ordinal);
        Assert.DoesNotContain("CoverArtUrl = download.CoverArtUrl", downloads, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticPlaylistMatching_QueriesOnlyPlaybackCapableProviders()
    {
        var walker = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Spotify", "PerProviderTrackMatcher.cs"));
        var providers = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Common", "MultiProviderMetadataService.cs"));

        Assert.Contains("GetEnabledPlaybackProviders()", providers, StringComparison.Ordinal);
        Assert.Contains("requirePlayableExtensions: true", providers, StringComparison.Ordinal);
        Assert.Contains("InjectedSourceTrack", walker, StringComparison.Ordinal);
        Assert.Contains("PerProviderAcceptThresholds", walker, StringComparison.Ordinal);
        Assert.Contains("CanUseForPlayback", walker, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveProviderSearch_UsesTheTypedExtensionAwareGateway()
    {
        var controller = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "TrackMatchesController.cs"));

        Assert.Contains("IProtocolProviderGateway", controller, StringComparison.Ordinal);
        Assert.Contains("ProviderCapabilityKind.Streaming", controller, StringComparison.Ordinal);
        Assert.Contains("ProviderCapabilityKind.Download", controller, StringComparison.Ordinal);
        Assert.Contains("providerGateway.SearchPlayableSongsAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("PerProviderTrackMatcher.SearchPlayableAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSources_AreProjectedIntoTheDurableIdentityGraph()
    {
        var orchestration = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Playlists", "PlaylistOrchestrationService.cs"));
        var service = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Matching", "TrackMatchCommandService.cs"));

        Assert.Contains("TrackMatchDecisionEngine", orchestration, StringComparison.Ordinal);
        Assert.Contains("ITrackMatchRepository", orchestration, StringComparison.Ordinal);
        Assert.Contains("PlaylistMaterializationJobHandler", orchestration, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistAutomatedTrackMatchCommand", service, StringComparison.Ordinal);
        Assert.Contains("MatchSourceTracksAsync", service, StringComparison.Ordinal);
        Assert.Contains("TrackMatchRecord", service, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMappingConvergence_PreservesEvidenceAndContinuesThroughNormalMatching()
    {
        var service = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Matching", "TrackMatchCommandService.cs"));

        Assert.Contains("DurableProviderRoute", service, StringComparison.Ordinal);
        Assert.Contains("TrackMatchDetailData", service, StringComparison.Ordinal);
        Assert.Contains("TrackMatchActivityData", service, StringComparison.Ordinal);
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
        Assert.Contains("TimeSpan.FromMinutes(15)", indexing, StringComparison.Ordinal);
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
