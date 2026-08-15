namespace allstarr.Tests;

public sealed class SpotifyPlaylistDiscoveryWebContractTests
{
    private readonly string controller = File.ReadAllText(
        FindRepositoryFile("allstarr", "Controllers", "PlaylistLinksController.cs"));
    [Fact]
    public void InitialPlaylistBrowse_ExhaustsProviderPagesAndDeduplicatesStableIds()
    {
        Assert.Contains("[FromQuery] int limit = 100", controller, StringComparison.Ordinal);
        Assert.Contains("const int maximumPages = 40", controller, StringComparison.Ordinal);
        Assert.Contains("seenPlaylistIds.Add(item.Id)", controller, StringComparison.Ordinal);
        Assert.Contains("BuildProviderPlaylistDiscoveryKey", controller, StringComparison.Ordinal);
        Assert.Contains("requestedCursor != null || !page.IsPartial", controller, StringComparison.Ordinal);
        Assert.Contains("currentCursor = nextCursor", controller, StringComparison.Ordinal);
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
