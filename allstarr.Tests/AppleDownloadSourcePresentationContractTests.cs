namespace allstarr.Tests;

public sealed class AppleDownloadSourcePresentationContractTests
{
    private readonly string controller = File.ReadAllText(
        FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));
    private readonly string script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void AppleGateway_IsPresentedAsAnOperatorManagedSource()
    {
        Assert.Contains("Categories = [\"metadata\", \"streaming\", \"download\", \"lyrics\"]", controller, StringComparison.Ordinal);
        Assert.Contains("ConnectionKind = \"operator_managed\"", controller, StringComparison.Ordinal);
        Assert.Contains("Audience = \"everyone\"", controller, StringComparison.Ordinal);
        Assert.Contains("ImplementationOrigin = \"built_in\"", controller, StringComparison.Ordinal);
        Assert.Contains("RouteId = \"builtin:apple-download\"", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void SourcePresentation_ExposesOwnershipAndRouteFactsWithoutCrowdingTheCard()
    {
        Assert.Contains("Operator managed${audience ? ` · ${titleCase(audience)}`", script, StringComparison.Ordinal);
        Assert.Contains("providerId === \"apple-download\" ? this.renderAppleMusicManager()", script, StringComparison.Ordinal);
        Assert.Contains("this.appleMusicStatus.logged_in", script, StringComparison.Ordinal);
        Assert.Contains("Discovered Apple download capabilities", script, StringComparison.Ordinal);
        Assert.Contains("Configure this source and verify each supported capability.", script, StringComparison.Ordinal);
        Assert.Contains("<summary>Advanced source details</summary>", script, StringComparison.Ordinal);
        Assert.Contains("<dt>Route ID</dt>", script, StringComparison.Ordinal);
        Assert.Contains("<dt>Implementation</dt>", script, StringComparison.Ordinal);
        Assert.Contains("<dt>Audience</dt>", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."), Path.Combine(parts));
    }
}
