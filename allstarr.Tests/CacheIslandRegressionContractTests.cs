namespace allstarr.Tests;

public sealed class CacheIslandRegressionContractTests
{
    private readonly string _repositoryRoot = FindRepositoryRoot();

    [Fact]
    public void PlaylistSummary_UsesSharedApplicationCacheInsteadOfPrivateFile()
    {
        var controller = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Controllers",
            "PlaylistController.cs"));
        var mapping = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Core",
            "Matching",
            "TrackMatchCommandService.cs"));
        var helper = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Admin",
            "AdminHelperService.cs"));

        Assert.Contains("BuildAdminPlaylistSummaryKey", controller, StringComparison.Ordinal);
        Assert.Contains("SetStringAsync(playlistSummaryKey", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("admin_playlists_summary.json", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("admin_playlists_summary.json", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("admin_playlists_summary.json", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackMetadataAndArtwork_UseSharedBoundedCaches()
    {
        var external = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Common",
            "ExternalPlaybackMetadataResolver.cs"));
        var jellyfin = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Jellyfin",
            "JellyfinPlaybackMetadataResolver.cs"));

        Assert.Contains("IApplicationCache cache", external, StringComparison.Ordinal);
        Assert.Contains("BuildPlaybackMetadataKey", external, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", external, StringComparison.Ordinal);
        Assert.Contains("IApplicationCache cache", jellyfin, StringComparison.Ordinal);
        Assert.Contains("BuildPlaybackMetadataKey", jellyfin, StringComparison.Ordinal);
        Assert.Contains("IMediaAssetResolver mediaAssets", jellyfin, StringComparison.Ordinal);
        Assert.Contains("MediaAssetIdentity", jellyfin, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", jellyfin, StringComparison.Ordinal);
    }

    [Fact]
    public void JellyfinEndpointPolicy_UsesSharedItemTypeCache()
    {
        var middleware = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Middleware",
            "JellyfinMusicEndpointPolicyMiddleware.cs"));

        Assert.Contains("IApplicationCache cache", middleware, StringComparison.Ordinal);
        Assert.Contains("BuildJellyfinItemTypeKey", middleware, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", middleware, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackSignalDedupe_UsesExpiringHashedSharedCacheKeys()
    {
        var controller = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Controllers",
            "JellyfinController.PlaybackSessions.cs"));
        var keys = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Common",
            "CacheKeyBuilder.cs"));

        Assert.Contains("BuildPlaybackSignalDedupeKey", controller, StringComparison.Ordinal);
        Assert.Contains("PlaybackSignalDedupeWindow", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RecentPlaybackSignals", controller, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", keys, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyPlaylistArtworkDescriptors_UseSharedRevisionScopedCache()
    {
        var client = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Core",
            "Providers",
            "Spotify",
            "SpotifyPathfinderPlaylistClient.cs"));

        Assert.Contains("IApplicationCache? cache", client, StringComparison.Ordinal);
        Assert.Contains("BuildProviderPlaylistArtworkDescriptorKey", client, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", client, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate allstarr.sln");
    }
}
