namespace allstarr.Tests;

public sealed class ProtocolProviderGatewayContractTests
{
    [Fact]
    public void JellyfinExternalRoutesUseProviderGatewayWhileNativeAudioRemainsRelay()
    {
        var root = RepositoryRoot();
        var controller = File.ReadAllText(Path.Combine(
            root, "allstarr", "Controllers", "JellyfinController.cs"));
        var search = File.ReadAllText(Path.Combine(
            root, "allstarr", "Controllers", "JellyfinController.Search.cs"));
        var audio = File.ReadAllText(Path.Combine(
            root, "allstarr", "Controllers", "JellyfinController.Audio.cs"));

        Assert.Contains("_providerGateway.GetSongAsync", controller, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.GetArtistAlbumsAsync", controller, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.GetArtistTracksAsync", controller, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.SearchPlayableSongsAsync", controller, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.SearchAsync", search, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.SearchPlaylistsAsync", search, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.GetPlaylistAsync", File.ReadAllText(Path.Combine(
            root, "allstarr", "Controllers", "JellyfinController.PlaylistHandler.cs")), StringComparison.Ordinal);
        Assert.Contains("_providerGateway.OpenStreamAsync", audio, StringComparison.Ordinal);
        Assert.Contains("return await ProxyJellyfinStream(fullPath, itemId);", audio, StringComparison.Ordinal);
        Assert.True(audio.IndexOf("_providerGateway.OpenStreamAsync", StringComparison.Ordinal) <
                    audio.IndexOf("_downloadService.DownloadAndStreamAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void JellyfinExternalArtworkDefersMetadataUntilMediaCacheMiss()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "allstarr", "Controllers", "JellyfinController.cs"));
        var start = source.IndexOf(
            "private async Task<ResolvedMediaAsset?> ResolveExternalImageAsync",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private IActionResult CreateFormattedImageResponse",
            start,
            StringComparison.Ordinal);
        var method = source[start..end];

        Assert.True(
            method.IndexOf("_mediaAssets.ResolveAsync", StringComparison.Ordinal) <
            method.IndexOf("GetProviderSongForImageAsync", StringComparison.Ordinal));
        Assert.True(
            method.IndexOf("_mediaAssets.ResolveAsync", StringComparison.Ordinal) <
            method.IndexOf("GetProviderPlaylistForImageAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void SubsonicExternalRoutesUseProviderGatewayWhileNativeAudioRemainsRelay()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "allstarr", "Controllers", "SubSonicController.cs"));

        Assert.Contains("_providerGateway.SearchAsync", source, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.SearchPlaylistsAsync", source, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.GetPlaylistAsync", source, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.GetSongAsync", source, StringComparison.Ordinal);
        Assert.Contains("_providerGateway.OpenStreamAsync", source, StringComparison.Ordinal);
        Assert.Contains("return await _proxyService.RelayStreamAsync(parameters", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("_providerGateway.OpenStreamAsync", StringComparison.Ordinal) <
                    source.IndexOf("_downloadService.DownloadAndStreamAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void CachedPlaybackIsQualitySafeAndPlaylistReadsDoNotPrepareAudio()
    {
        var root = RepositoryRoot();
        var audio = File.ReadAllText(Path.Combine(
            root, "allstarr", "Controllers", "JellyfinController.Audio.cs"));
        var externalStart = audio.IndexOf(
            "private async Task<IActionResult> StreamExternalContent", StringComparison.Ordinal);
        var externalEnd = audio.IndexOf(
            "private static string MediaFileExtension", externalStart, StringComparison.Ordinal);
        var external = audio[externalStart..externalEnd];
        Assert.True(
            external.IndexOf("quality == StreamQuality.Original", StringComparison.Ordinal) <
            external.IndexOf("_providerGateway.OpenStreamAsync", StringComparison.Ordinal));
        Assert.True(
            external.IndexOf("_localLibraryService.GetLocalPathForExternalSongAsync", StringComparison.Ordinal) <
            external.IndexOf("_providerGateway.OpenStreamAsync", StringComparison.Ordinal));
        Assert.Contains("headOnly: HttpMethods.IsHead(Request.Method)", external, StringComparison.Ordinal);

        var subsonic = File.ReadAllText(Path.Combine(
            root, "allstarr", "Controllers", "SubSonicController.cs"));
        var streamStart = subsonic.IndexOf("public async Task<IActionResult> Stream()", StringComparison.Ordinal);
        var streamEnd = subsonic.IndexOf("Returns external song info", streamStart, StringComparison.Ordinal);
        var stream = subsonic[streamStart..streamEnd];
        Assert.True(
            stream.IndexOf("requestedQuality == ProviderAudioQuality.Any", StringComparison.Ordinal) <
            stream.IndexOf("_providerGateway.OpenStreamAsync", StringComparison.Ordinal));
        Assert.True(
            stream.IndexOf("_localLibraryService.GetLocalPathForExternalSongAsync", StringComparison.Ordinal) <
            stream.IndexOf("_providerGateway.OpenStreamAsync", StringComparison.Ordinal));
        Assert.Contains("headOnly: HttpMethods.IsHead(Request.Method)", stream, StringComparison.Ordinal);

        var playlists = File.ReadAllText(Path.Combine(
            root, "allstarr", "Controllers", "JellyfinController.PlaylistHandler.cs"));
        Assert.DoesNotContain("OpenStreamAsync", playlists, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadAndStreamAsync", playlists, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistCompatibilityIsFilteredByExactAccountResolution()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "allstarr", "Core", "Protocols", "ProtocolProviderGateway.cs"));

        Assert.Contains("ResolveAllowedCompatibilityProvidersAsync", source, StringComparison.Ordinal);
        Assert.Contains("accounts.ResolveAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy.Songs.Where(item => Allowed", source, StringComparison.Ordinal);
        Assert.Contains("var routedProviderId = NormalizeProvider(providerId)", source, StringComparison.Ordinal);
        Assert.Contains("RequireCompatibilityProviderAsync(protocol, routedProviderId)", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeProvider(item).Equals(normalized", source, StringComparison.Ordinal);
        Assert.Contains("ProviderCapabilityKind.Playlist", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAllowedCompatibilityProvidersAsync(\n            protocol, actor, ProviderCapabilityKind.Playlist)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceUri = {", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedMetadataLookupsDoNotFallbackToLegacy()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "allstarr", "Core", "Protocols", "ProtocolProviderGateway.cs"));
        var methods = new[]
        {
            "public async Task<Song?> GetSongAsync(",
            "public async Task<Artist?> GetArtistAsync(",
            "public async Task<List<Album>> GetArtistAlbumsAsync(",
            "public async Task<List<Song>> GetArtistTracksAsync("
        };

        foreach (var method in methods)
        {
            var methodStart = source.IndexOf(method, StringComparison.Ordinal);
            var authenticatedStart = source.IndexOf(
                "var routedProviderId = NormalizeProvider(providerId);",
                methodStart,
                StringComparison.Ordinal);
            var methodEnd = source.IndexOf(
                "\n    public async Task",
                authenticatedStart,
                StringComparison.Ordinal);

            Assert.True(methodStart >= 0 && authenticatedStart > methodStart && methodEnd > authenticatedStart, method);
            Assert.Contains(
                "await RequireCompatibilityProviderAsync(protocol, routedProviderId);",
                source[authenticatedStart..methodEnd],
                StringComparison.Ordinal);
            Assert.DoesNotContain("legacyMetadata", source[authenticatedStart..methodEnd], StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
