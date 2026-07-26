namespace allstarr.Tests;

public sealed class InjectedPlaylistResponsiveDesignContractTests
{
    [Fact]
    public void PlaylistTableBecomesLabeledCardsOnMobile()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "responsive.css"));

        Assert.Contains("class=\"playlist-main-cell\" data-label=\"Playlist\"", script, StringComparison.Ordinal);
        Assert.Contains("data-label=\"Last sync\"", script, StringComparison.Ordinal);
        Assert.Contains(".injected-data-table td::before", styles, StringComparison.Ordinal);
        Assert.Contains(".injected-data-table .actions-cell", styles, StringComparison.Ordinal);
        Assert.Contains(".playlist-action-menu[open] > div", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackRowsRemainVisibleAndActionableOnMobile()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "responsive.css"));
        var workspaceStyles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains("class=\"track-primary-action\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"track-byline\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"track-provider-cell\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"track-menu-cell\"", script, StringComparison.Ordinal);
        Assert.Contains(".playlist-track-head", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 30px minmax(0, 1fr)", styles, StringComparison.Ordinal);
        Assert.Contains(".playlist-track-row .shared-track-row-grid", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) minmax(130px, auto) 64px 44px", styles, StringComparison.Ordinal);
        Assert.Contains("place-items: center", workspaceStyles, StringComparison.Ordinal);
        Assert.Contains("height: 100dvh", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistDialogPrioritizesScrollableTracksAndSharedTabIndicators()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));
        var workspaceStyles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "workspaces.css"));
        var designStyles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "design-system.css"));

        Assert.Contains("syncSegmentedControls()", script, StringComparison.Ordinal);
        Assert.Contains("--tab-indicator-width", script, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: auto minmax(0, 1fr)", workspaceStyles, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", workspaceStyles, StringComparison.Ordinal);
        Assert.Contains(".segmented-ready::before", designStyles, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", designStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void PaginationUsesAWindowWithGapMarkers()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("const paginationPages = pageCount <= 7", script, StringComparison.Ordinal);
        Assert.Contains("class=\"pagination-gap\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"page-number ${item === page", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNumber === page", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Previous page\"", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Next page\"", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
