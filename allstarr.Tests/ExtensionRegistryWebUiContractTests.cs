namespace allstarr.Tests;

public sealed class ExtensionRegistryWebUiContractTests
{
    private readonly string _script = ReadRepositoryFile("allstarr", "wwwroot", "js", "webui.js");
    private readonly string _styles = ReadRepositoryFile("allstarr", "wwwroot", "css", "workspaces.css");

    [Fact]
    public void RegistryRemovalExplainsInstalledPackageDependencies()
    {
        Assert.Contains("removeExtensionRegistry: (registryId, expectedRevision)", _script, StringComparison.Ordinal);
        Assert.Contains("still supplies ${dependencies.length} installed package version", _script, StringComparison.Ordinal);
        Assert.Contains("Disable and uninstall them from the Installed tab", _script, StringComparison.Ordinal);
        Assert.Contains("extension-registry-dependencies", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRegistryUsesACompactHeaderActionAndEditor()
    {
        Assert.Contains("extensionRegistryFormOpen", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"primary compact icon-label\"", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"extension-registry-editor\"", _script, StringComparison.Ordinal);
        Assert.Contains(".extension-registry-editor > .actions", _styles, StringComparison.Ordinal);
        Assert.Contains("justify-content: flex-end", _styles, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
