namespace allstarr.Tests;

public sealed class PlaylistDetailFilterWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

    [Fact]
    public void TrackToolbar_FiltersByTextStateAndVisibleProvider()
    {
        Assert.Contains("playlist-track-toolbar", _script, StringComparison.Ordinal);
        Assert.Contains("Filter by match state", _script, StringComparison.Ordinal);
        Assert.Contains("Filter by playback provider", _script, StringComparison.Ordinal);
        Assert.Contains("case \"matched\"", _script, StringComparison.Ordinal);
        Assert.Contains("case \"unmatched\"", _script, StringComparison.Ordinal);
        Assert.Contains("case \"local\"", _script, StringComparison.Ordinal);
        Assert.Contains("case \"external\"", _script, StringComparison.Ordinal);
        Assert.Contains("providerOptions.map(([id, label])", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackToolbar_ResetsOnOpenAndOffersAccessibleClear()
    {
        var openPlaylistStart = _script.IndexOf("async openInjectedPlaylist(name)", StringComparison.Ordinal);
        var detailRendererStart = _script.IndexOf("renderInjectedPlaylistDetails()", openPlaylistStart, StringComparison.Ordinal);
        var openPlaylist = _script[openPlaylistStart..detailRendererStart];

        Assert.Contains("this.injectedTrackStateFilter = \"all\"", openPlaylist, StringComparison.Ordinal);
        Assert.Contains("this.injectedTrackProviderFilter = \"all\"", openPlaylist, StringComparison.Ordinal);
        Assert.Contains("role=\"search\" aria-label=\"Filter playlist tracks\"", _script, StringComparison.Ordinal);
        Assert.Contains("?disabled=${!trackFiltersActive}", _script, StringComparison.Ordinal);
        Assert.Contains(".playlist-track-toolbar {", _css, StringComparison.Ordinal);
        Assert.Contains("grid-column: 1 / -1", _css, StringComparison.Ordinal);
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
