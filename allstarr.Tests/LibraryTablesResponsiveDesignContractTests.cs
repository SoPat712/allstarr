namespace allstarr.Tests;

public sealed class LibraryTablesResponsiveDesignContractTests
{
    [Fact]
    public void LibraryWorkspaceTablesShareTheMobileCardContract()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "responsive.css"));

        Assert.True(CountOccurrences(script, "responsive-data-table") >= 5);
        Assert.Contains("class=\"mobile-primary\" data-label=\"Playlist\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"mobile-primary\" data-label=\"Provider track\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"mobile-primary\" data-label=\"Artist\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"row-actions mobile-actions\" data-label=\"Actions\"", script, StringComparison.Ordinal);
        Assert.Contains(".responsive-data-table td::before", styles, StringComparison.Ordinal);
        Assert.Contains(".responsive-data-table .empty-table-row", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryResponsiveLibraryTableHandlesItsEmptyState()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("No playlists yet.", script, StringComparison.Ordinal);
        Assert.Contains("Provider links and existing injected playlists live in one workspace.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Imported configuration", script, StringComparison.Ordinal);
        Assert.Contains("No mappings found.", script, StringComparison.Ordinal);
        Assert.Contains("Review match", script, StringComparison.Ordinal);
        Assert.Contains("Needs attention", script, StringComparison.Ordinal);
        Assert.Contains("[\"cached\", \"Cached\", \"download\"]", script, StringComparison.Ordinal);
        Assert.Contains("renderManagedDownloads", script, StringComparison.Ordinal);
        Assert.Contains("bitrateKbps", script, StringComparison.Ordinal);
        Assert.Contains("promoteCachedDownload", script, StringComparison.Ordinal);
        Assert.Contains("No playlist data loaded.", script, StringComparison.Ordinal);
        Assert.True(CountOccurrences(script, "class=\"empty-table-row\"") >= 3);
    }

    [Fact]
    public void PlaylistTablesShareAccessibleSortAndDesktopDividerContracts()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains("renderPlaylistSortHeader", script, StringComparison.Ordinal);
        Assert.Contains("aria-sort=${direction}", script, StringComparison.Ordinal);
        Assert.Contains("setPlaylistTableSort(table, key)", script, StringComparison.Ordinal);
        Assert.Contains("if (table === \"imported\") this.injectedPage = 1;", script, StringComparison.Ordinal);
        Assert.Contains("cache: { key: \"updated\", direction: \"descending\" }", script, StringComparison.Ordinal);
        Assert.Contains("kept: { key: \"updated\", direction: \"descending\" }", script, StringComparison.Ordinal);
        Assert.Contains("this.sortPlaylistTableRows(mode", script, StringComparison.Ordinal);
        Assert.Contains("this.renderPlaylistSortHeader(mode, \"quality\", \"Quality\")", script, StringComparison.Ordinal);
        Assert.Contains("this.renderPlaylistSortHeader(mode, \"updated\", \"Updated\")", script, StringComparison.Ordinal);
        Assert.Contains("@media (min-width: 761px)", styles, StringComparison.Ordinal);
        Assert.Contains("--table-cell-block", styles, StringComparison.Ordinal);
        Assert.Contains("--table-cell-inline", styles, StringComparison.Ordinal);
        Assert.Contains(".aligned-data-table th + th", styles, StringComparison.Ordinal);
        Assert.Contains(".aligned-data-table td + td", styles, StringComparison.Ordinal);
        Assert.Contains("responsive-data-table aligned-data-table sortable-data-table", script, StringComparison.Ordinal);
        Assert.Contains("injected-data-table aligned-data-table sortable-data-table", script, StringComparison.Ordinal);
        Assert.Contains("responsive-data-table aligned-data-table mapping-data-table", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryNavigation_HasOneFourTabTaxonomy()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("[\"playlists\", \"Playlists\", \"playlist\"]", script, StringComparison.Ordinal);
        Assert.Contains("[\"mappings\", \"Mappings\", \"sources\"]", script, StringComparison.Ordinal);
        Assert.Contains("[\"cached\", \"Cached\", \"download\"]", script, StringComparison.Ordinal);
        Assert.Contains("[\"kept\", \"Kept\", \"check\"]", script, StringComparison.Ordinal);
        Assert.Contains("[\"playlists\", \"link\", \"injected\", \"external\"].includes(requestedSub)",
            script, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"link\", \"Playlist links\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"injected\", \"Injected\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"external\", \"External playlists\"", script, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
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
