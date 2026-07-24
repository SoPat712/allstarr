namespace allstarr.Tests;

public sealed class SourcePriorityCopyWebUiContractTests
{
    private readonly string script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string controller = File.ReadAllText(
        FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));

    [Fact]
    public void SourcePriority_UsesSourceLanguageInVisibleInstructions()
    {
        Assert.Contains("which source fills a missing track", script, StringComparison.Ordinal);
        Assert.Contains("Drag sources top-to-bottom to set order", script, StringComparison.Ordinal);
        Assert.DoesNotContain("which provider fills a missing track", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Drag providers top-to-bottom to set order", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PriorityGroupDescriptions_UseSourceLanguage()
    {
        Assert.Contains("which source fills a missing track", controller, StringComparison.Ordinal);
        Assert.Contains("which source plays a missing track", controller, StringComparison.Ordinal);
        Assert.Contains("playlist tracks from each source.", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist tracks from each source provider", controller, StringComparison.Ordinal);
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
