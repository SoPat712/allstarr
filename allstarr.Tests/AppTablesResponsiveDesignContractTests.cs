namespace allstarr.Tests;

public sealed class AppTablesResponsiveDesignContractTests
{
    [Fact]
    public void OperationalTablesUseTheSharedResponsiveContract()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("class=\"mobile-primary\" data-label=\"Type\"", script, StringComparison.Ordinal);
        Assert.Contains("data-label=\"Runs and budgets\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"mono mobile-primary\" data-label=\"Endpoint\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"mono mobile-primary\" data-label=\"Legacy key\"", script, StringComparison.Ordinal);
        Assert.Contains("data-label=\"Outcome\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyEndpointUsageUsesTheResponsiveEmptyRow()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("class=\"empty-table-row\"><td class=\"empty-table-cell\" colspan=\"2\"", script, StringComparison.Ordinal);
        Assert.Contains("No endpoint usage data.", script, StringComparison.Ordinal);
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
