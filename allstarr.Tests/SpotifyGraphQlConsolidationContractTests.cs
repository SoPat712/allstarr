namespace allstarr.Tests;

public sealed class SpotifyGraphQlConsolidationContractTests
{
    private readonly string source = File.ReadAllText(
        FindRepositoryFile("allstarr", "Services", "Spotify", "SpotifyApiClient.cs"));

    [Fact]
    public void CompatibilityClient_DelegatesPlaylistDiscoveryToSharedPathfinderTransport()
    {
        Assert.Contains(
            "var pathfinder = new SpotifyPathfinderPlaylistClient(_webApiClient",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ProviderPageRequest(100, cursor)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "seenPlaylistIds.Add(item.Id.Value)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "seenCursors.Add(cursor)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibilityClient_NoLongerMaintainsASecondLibraryV3Parser()
    {
        var methodStart = source.IndexOf(
            "public async Task<List<SpotifyPlaylist>> GetUserPlaylistsAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static DateTime? TryGetSpotifyPlaylistCreatedAt",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.DoesNotContain("\"By Spotify\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("operationName\", \"libraryV3", method, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetProperty(\"libraryV3\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibilityPlaylistTracks_UseTheSharedTypedPathfinderTransport()
    {
        Assert.Contains(
            "pathfinder.GetPlaylistTracksAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ProviderPlaylistTracksRequest(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "BuildCompatibilityPlaylist(summary, tracks, artworkUrl)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FetchPlaylistViaGraphQLAsync",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FetchPlaylistMetadataAsync",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FetchPlaylistTracksPageAsync",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ParseGraphQLTrack",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."), Path.Combine(parts));
    }
}
