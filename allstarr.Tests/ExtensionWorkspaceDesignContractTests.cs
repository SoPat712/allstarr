namespace allstarr.Tests;

public sealed class ExtensionWorkspaceDesignContractTests
{
    [Fact]
    public void InstalledWorkspaceUsesExpandablePackageAwareActivity()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "js", "webui.js"));

        Assert.DoesNotContain("extension-capability-legend", script, StringComparison.Ordinal);
        Assert.Contains("class=\"extension-activity-entry", script, StringComparison.Ordinal);
        Assert.Contains("Open extension details", script, StringComparison.Ordinal);
        Assert.Contains("Recent extension activity", script, StringComparison.Ordinal);
        Assert.Contains("Extension runtime", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityDetailsAdaptToMobile()
    {
        var root = FindRepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains(".extension-activity-entry[open] summary", styles, StringComparison.Ordinal);
        Assert.Contains(".extension-activity-entry dl", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr)", styles, StringComparison.Ordinal);
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
