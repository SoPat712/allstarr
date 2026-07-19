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
    public void Extensions_PresentDedicatedStoreAndModalSafetyFlows()
    {
        Assert.Contains("Install extension", _script, StringComparison.Ordinal);
        Assert.Contains("Extension installed and enabled", _script, StringComparison.Ordinal);
        Assert.Contains("Review permissions", _script, StringComparison.Ordinal);
        Assert.Contains("Allow all requested access", _script, StringComparison.Ordinal);
        Assert.Contains("Save choices and enable", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"panel extension-permission-dialog\"", _script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Close permission review\"", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"panel extension-install-dialog\"", _script, StringComparison.Ordinal);
        Assert.Contains("this.extensionViewTab", _script, StringComparison.Ordinal);
        Assert.Contains("/settings/extensions", _script, StringComparison.Ordinal);
        Assert.Contains("Verify and install", _script, StringComparison.Ordinal);
        Assert.Contains("expectedRevision", _script, StringComparison.Ordinal);
        Assert.Contains("Previous extension version restored", _script, StringComparison.Ordinal);
        Assert.Contains("Registry JSON URL", _script, StringComparison.Ordinal);
        Assert.Contains("this.extensionRegistryError = error.message", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", _script, StringComparison.Ordinal);
        Assert.Contains("Extension registry validated and added", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("Extension control plane", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("Stage a package", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionWorkspace_IsResponsiveAndWrapsUntrustedValues()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".extension-workspace-grid", css, StringComparison.Ordinal);
        Assert.Contains(".extension-install-dialog", css, StringComparison.Ordinal);
        Assert.Contains(".extension-value", css, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 760px)", css, StringComparison.Ordinal);
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
