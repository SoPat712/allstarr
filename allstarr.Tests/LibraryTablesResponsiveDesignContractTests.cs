namespace allstarr.Tests;

public sealed class LibraryTablesResponsiveDesignContractTests
{
    [Fact]
    public void LibraryWorkspaceTablesShareTheMobileCardContract()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "responsive.css"));

        Assert.True(CountOccurrences(script, "class=\"responsive-data-table\"") >= 5);
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
        Assert.Contains("No playlist data loaded.", script, StringComparison.Ordinal);
        Assert.True(CountOccurrences(script, "class=\"empty-table-row\"") >= 3);
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
