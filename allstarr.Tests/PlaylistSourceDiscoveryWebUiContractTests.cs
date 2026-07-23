namespace allstarr.Tests;

public sealed class PlaylistSourceDiscoveryWebUiContractTests
{
    private readonly string _script = ReadRepositoryFile("allstarr", "wwwroot", "js", "webui.js");

    [Fact]
    public void PlaylistDiscoverySeparatesProviderFailuresFromEmptyAccounts()
    {
        Assert.Contains("sourceAccountId: accountId, sourcePlaylist: null, sourceQuery: \"\", sourceNextCursor: \"\", loading: true, error: \"\"",
            _script, StringComparison.Ordinal);
        Assert.Contains("if (!items.length && this.playlistWizard.error) return nothing;",
            _script, StringComparison.Ordinal);
        Assert.Contains("No playlists found for this account. Try a search.",
            _script, StringComparison.Ordinal);
        Assert.DoesNotContain("No playlists found. Try a search or choose another account.",
            _script, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            relativePath));
        return File.ReadAllText(path);
    }
}
