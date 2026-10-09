namespace allstarr.Tests;

public sealed class CacheIslandRegressionContractTests
{
    private readonly string _repositoryRoot = FindRepositoryRoot();

    [Fact]
    public void PlaylistSummary_UsesDurableProjectionWithoutCachedViewModel()
    {
        var orchestration = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Core",
            "Playlists",
            "PlaylistOrchestrationService.cs"));
        var keys = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Common",
            "CacheKeyBuilder.cs"));

        Assert.DoesNotContain("BuildAdminPlaylistSummaryKey", orchestration, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildAdminPlaylistSummaryKey", keys, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalPlaybackCachesRemainBoundedWhileNativeReadsRequireFreshViewerAuthorization()
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
        var downloads = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Controllers",
            "DownloadActivityController.cs"));

        Assert.Contains("IApplicationCache cache", external, StringComparison.Ordinal);
        Assert.Contains("BuildPlaybackMetadataKey", external, StringComparison.Ordinal);
        Assert.Contains("_inflight.TryRemove", external, StringComparison.Ordinal);
        Assert.DoesNotContain("IApplicationCache", jellyfin, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPlaybackMetadataKey", jellyfin, StringComparison.Ordinal);
        Assert.DoesNotContain("_inflight", jellyfin, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshStaleAsync", jellyfin, StringComparison.Ordinal);
        Assert.Contains("IBackendLibraryAccessResolver libraryAccess", jellyfin, StringComparison.Ordinal);
        Assert.Contains("IBackendLibraryPermissionSource permissionSource", jellyfin, StringComparison.Ordinal);
        Assert.Contains("_libraryAccess.ResolveAsync(context", jellyfin, StringComparison.Ordinal);
        Assert.Contains("_permissionSource.CreateRequestAsync(context", jellyfin, StringComparison.Ordinal);
        Assert.Contains("Items?UserId=", jellyfin, StringComparison.Ordinal);
        Assert.Contains("IMediaAssetResolver mediaAssets", downloads, StringComparison.Ordinal);
        Assert.Contains("MediaAssetIdentity", downloads, StringComparison.Ordinal);
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
            "Providers",
            "Spotify",
            "SpotifyPathfinderPlaylistClient.cs"));

        Assert.Contains("IApplicationCache? cache", client, StringComparison.Ordinal);
        Assert.Contains("BuildProviderPlaylistArtworkDescriptorKey", client, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", client, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporaryAudio_UsesConfiguredRootPolicyTtlAndQualityIdentity()
    {
        var legacyDownload = File.ReadAllText(Path.Combine(
            _repositoryRoot, "allstarr", "Providers", "Qobuz", "QobuzDownloadService.cs"));
        var deezerMedia = File.ReadAllText(Path.Combine(
            _repositoryRoot, "allstarr", "Providers", "Deezer", "DeezerMediaClient.cs"));
        var deezerDownload = File.ReadAllText(Path.Combine(
            _repositoryRoot, "allstarr", "Providers", "Deezer", "DeezerDownloadCapabilityAdapter.cs"));
        var cleanup = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Common",
            "CacheCleanupService.cs"));

        Assert.Contains("Path.Combine(DownloadPath, \"transcoded\")", legacyDownload, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.Combine(\"downloads\", \"transcoded\")", legacyDownload, StringComparison.Ordinal);
        Assert.Contains("quality.ToString().ToLowerInvariant()", legacyDownload, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadPath", deezerMedia, StringComparison.Ordinal);
        Assert.DoesNotContain("FileStream", deezerMedia, StringComparison.Ordinal);
        Assert.Contains("artifacts.WriteProducedAsync", deezerDownload, StringComparison.Ordinal);
        Assert.Contains("request.Workspace", deezerDownload, StringComparison.Ordinal);
        Assert.Contains("Quality(request.RequestedQuality, configuredQuality)", deezerDownload, StringComparison.Ordinal);
        Assert.Contains("CacheExtensions.TranscodeCacheTTL", cleanup, StringComparison.Ordinal);
        Assert.Contains("_subsonicSettings.StorageMode == StorageMode.Cache", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("CacheCleanupService disabled", cleanup, StringComparison.Ordinal);
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
