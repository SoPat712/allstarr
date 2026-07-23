namespace allstarr.Tests;

public sealed class ExtensionPermissionReviewDesignContractTests
{
    [Fact]
    public void PermissionReviewUsesStructuredResponsiveCards()
    {
        var root = FindRepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains(".extension-permission-dialog .permission-summary", styles, StringComparison.Ordinal);
        Assert.Contains(".extension-permission-dialog .extension-permission-row", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto", styles, StringComparison.Ordinal);
        Assert.Contains(".extension-permission-dialog .permission-confirm", styles, StringComparison.Ordinal);
        Assert.Contains(".extension-permission-dialog > .dialog-actions", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionReviewStacksDecisionsOnMobile()
    {
        var root = FindRepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(root, "allstarr", "wwwroot", "css", "workspaces.css"));

        Assert.Contains("width: min(100vw - (2 * var(--space-3)), 760px)", styles, StringComparison.Ordinal);
        Assert.Contains(".extension-permission-dialog .extension-permission-row .row-actions", styles, StringComparison.Ordinal);
        Assert.Contains("width: 100%", styles, StringComparison.Ordinal);
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
