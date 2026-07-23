namespace allstarr.Tests;

public sealed class WebUiPerformanceAndPolishContractTests
{
    private readonly string _script = ReadRepositoryFile("allstarr", "wwwroot", "js", "webui.js");
    private readonly string _responsive = ReadRepositoryFile("allstarr", "wwwroot", "css", "responsive.css");

    [Fact]
    public void HiddenDocumentsPauseTheNowPlayingClock()
    {
        Assert.Contains("document.addEventListener(\"visibilitychange\", this.onVisibilityChange)", _script, StringComparison.Ordinal);
        Assert.Contains("if (document.hidden) this.stopNowPlayingClock()", _script, StringComparison.Ordinal);
        Assert.Contains("document.removeEventListener(\"visibilitychange\", this.onVisibilityChange)", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtensionAndSetupRenderingUsePrecomputedLookups()
    {
        Assert.Contains("const packageById = new Map()", _script, StringComparison.Ordinal);
        Assert.Contains("const latestStoreByExtension = new Map()", _script, StringComparison.Ordinal);
        Assert.Contains("const healthyProviderIds = new Set", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("storeItems.filter((candidate)", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("const observed = this.providerHealth.filter", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void SmallestBreakpointDoesNotRestoreDesktopInjectedTableWidth()
    {
        Assert.DoesNotContain(".injected-data-table {\n        min-width: 1040px;", _responsive, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
