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
    public void CompatibilityMetadataIsFilteredByExactAccountResolution()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "allstarr", "Core", "Protocols", "ProtocolProviderGateway.cs"));

        Assert.Contains("ResolveAllowedCompatibilityProvidersAsync", source, StringComparison.Ordinal);
        Assert.Contains("accounts.ResolveAsync", source, StringComparison.Ordinal);
        Assert.Contains("legacy.Songs.Where(item => Allowed", source, StringComparison.Ordinal);
        Assert.Contains("RequireCompatibilityProviderAsync(protocol, providerId)", source, StringComparison.Ordinal);
        Assert.Contains("ProviderCapabilityKind.Playlist", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAllowedCompatibilityProvidersAsync(\n            protocol, actor, ProviderCapabilityKind.Playlist)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceUri = {", source, StringComparison.Ordinal);
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
