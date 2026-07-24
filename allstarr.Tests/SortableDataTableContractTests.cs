namespace allstarr.Tests;

public sealed class SortableDataTableContractTests
{
    private readonly string script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Theory]
    [InlineData("missing", "playlist", "Playlist")]
    [InlineData("missing", "missing", "Missing")]
    [InlineData("jobs", "type", "Type")]
    [InlineData("jobs", "finished", "Available / finished")]
    [InlineData("endpoints", "endpoint", "Endpoint")]
    [InlineData("endpoints", "count", "Count")]
    public void UsefulOperationalTables_UseSharedSortableHeaders(
        string table,
        string key,
        string label)
    {
        Assert.Contains(
            $"this.renderPlaylistSortHeader(\"{table}\", \"{key}\", \"{label}\")",
            script,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("jobs")]
    [InlineData("endpoints")]
    public void UsefulOperationalTables_UseSharedStableSorter(string table)
    {
        Assert.Matches(
            $@"this\.sortPlaylistTableRows\(\s*""{table}"",",
            script);
    }

    [Fact]
    public void SortHeaders_ExposeDirectionAndReverseAction()
    {
        Assert.Contains("aria-sort=${direction}", script, StringComparison.Ordinal);
        Assert.Contains("activate to reverse", script, StringComparison.Ordinal);
        Assert.Contains("sortable-data-table", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate the repository root."),
            Path.Combine(parts));
    }
}
