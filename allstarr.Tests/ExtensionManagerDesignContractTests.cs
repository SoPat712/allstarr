namespace allstarr.Tests;

public sealed class ExtensionManagerDesignContractTests
{
    [Fact]
    public void Manager_ShowsPackageFactsAndCapabilityAvailability()
    {
        var script = Read("allstarr/wwwroot/js/webui.js");

        Assert.Contains("class=\"extension-package-facts\"", script, StringComparison.Ordinal);
        Assert.Contains("Extension ID", script, StringComparison.Ordinal);
        Assert.Contains("class=\"extension-capability-matrix\"", script, StringComparison.Ordinal);
        Assert.Contains("Not declared", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerActivity_IsScopedExpandableAndDetailed()
    {
        var script = Read("allstarr/wwwroot/js/webui.js");
        var styles = Read("allstarr/wwwroot/css/workspaces.css");

        Assert.Contains("entry.extensionPackageId", script, StringComparison.Ordinal);
        Assert.Contains("extensionActivity.map((entry) => html`<details>", script, StringComparison.Ordinal);
        Assert.Contains("No additional details were recorded.", script, StringComparison.Ordinal);
        Assert.Contains("View all extension activity", script, StringComparison.Ordinal);
        Assert.Contains(".extension-manager-activity details[open]", styles, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
