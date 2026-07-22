namespace allstarr.Tests;

public sealed class WebUiExtensionActivityContractTests
{
    [Fact]
    public void ExtensionActivity_UsesServerSummaryAndNeverExposesRawEventCodes()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("entry.summary || entry.Summary || \"Extension event\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("titleCase(entry.eventCode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("title=${entry.eventCode", source, StringComparison.Ordinal);

        var controller = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "allstarr", "Controllers", "ExtensionController.cs"));
        Assert.Contains("Provider search", controller, StringComparison.Ordinal);
        Assert.Contains("failed ({badResponse.Groups[\"status\"].Value})", controller, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "allstarr.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Allstarr repository root.");
    }
}
