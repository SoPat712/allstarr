namespace allstarr.Tests;

public sealed class ExtensionSourceProjectionContractTests
{
    private readonly string _controller = File.ReadAllText(
        FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));
    private readonly string _model = File.ReadAllText(
        FindRepositoryFile("allstarr", "Models", "Admin", "AdminUiSchema.cs"));
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void ExtensionImplementations_MergeIntoStableSourceIdentity()
    {
        Assert.Contains("List<AdminUiProviderCapabilityRoute> CapabilityRoutes", _model, StringComparison.Ordinal);
        Assert.Contains("provider.Id.Equals(item.Id, StringComparison.Ordinal)", _controller, StringComparison.Ordinal);
        Assert.Contains("existing.CapabilityRoutes.Add(route)", _controller, StringComparison.Ordinal);
        Assert.Contains("existing.Categories = existing.Categories", _controller, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "providers.AddRange(_providerRegistry.Providers",
            _controller,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceDetail_ShowsIntentionalAlternativeRoutes()
    {
        Assert.Contains("capabilityRoutes.length > 1", _script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Capability routes\"", _script, StringComparison.Ordinal);
        Assert.Contains("route.capabilities || route.Capabilities", _script, StringComparison.Ordinal);
        Assert.Contains("route.routeId || route.RouteId", _script, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."),
            Path.Combine(parts));
    }
}
