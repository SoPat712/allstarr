namespace allstarr.Tests;

public sealed class WebUiExtensionControlPlaneContractTests
{
    private readonly string _script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void Extensions_UseDurableControlPlaneRoutes()
    {
        Assert.Contains("/api/admin/extensions/registries", _script, StringComparison.Ordinal);
        Assert.Contains("jsonBody({ enabled, expectedRevision }, \"PATCH\")", _script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/extensions/packages", _script, StringComparison.Ordinal);
        Assert.Contains("/permissions`,", _script, StringComparison.Ordinal);
        Assert.Contains("/review`,", _script, StringComparison.Ordinal);
        Assert.Contains("/activate`,", _script, StringComparison.Ordinal);
        Assert.Contains("/disable`,", _script, StringComparison.Ordinal);
        Assert.Contains("/rollback`,", _script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/extensions/logs?", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/admin/extensions/installed", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/admin/extensions/uninstall", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/admin/extensions/enable/", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void Extensions_RequireChecksumReviewAndRevisionedLifecycleActions()
    {
        Assert.Contains("Checksum published", _script, StringComparison.Ordinal);
        Assert.Contains("Verify and stage", _script, StringComparison.Ordinal);
        Assert.Contains("Every permission needs an explicit choice.", _script, StringComparison.Ordinal);
        Assert.Contains("expectedRevision", _script, StringComparison.Ordinal);
        Assert.Contains("Submit every decision", _script, StringComparison.Ordinal);
        Assert.Contains("Previous extension version restored", _script, StringComparison.Ordinal);
        Assert.Contains("Allstarr does not add one automatically.", _script, StringComparison.Ordinal);
        Assert.Contains("direct URL to an Allstarr registry JSON document", _script, StringComparison.Ordinal);
        Assert.Contains("this.extensionRegistryError = error.message", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", _script, StringComparison.Ordinal);
        Assert.Contains("Extension registry validated and added", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionWorkspace_CollapsesAndWrapsUntrustedValues()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".extension-control-grid", css, StringComparison.Ordinal);
        Assert.Contains(".extension-value", css, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", css, StringComparison.Ordinal);
        Assert.Contains(
            ".provider-grid,\n    .provider-account-grid,\n    .extension-control-grid",
            css,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. path]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(path)}");
    }
}
