namespace allstarr.Tests;

public sealed class ProviderCtsTrackRotationContractTests
{
    [Fact]
    public void Selector_UsesABoundedProviderSpecificCorpus()
    {
        var selector = Read("allstarr/Services/Common/ProviderCtsTrackSelector.cs");

        Assert.Contains("public const int CorpusLimit = 100", selector, StringComparison.Ordinal);
        Assert.Contains("item.ProviderId == providerId", selector, StringComparison.Ordinal);
        Assert.Contains("item.ResourceKind == ProviderResourceKind.Track", selector, StringComparison.Ordinal);
        Assert.Contains("(current + 1) % corpus.Length", selector, StringComparison.Ordinal);
        Assert.Contains("ProviderTrackIdentityId == selected.Id", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_AutomaticallySelectsATrackButKeepsManualOverride()
    {
        var controller = Read("allstarr/Controllers/ProviderDiagnosticsController.cs");
        var script = Read("allstarr/wwwroot/js/webui.js");

        Assert.Contains("trackSelector.SelectAsync", controller, StringComparison.Ordinal);
        Assert.Contains("selectionMode = automaticTrack == null ? \"manual\" : \"rotating-corpus\"", controller, StringComparison.Ordinal);
        Assert.Contains("public string? TrackId", controller, StringComparison.Ordinal);
        Assert.Contains("NoCache = true", controller, StringComparison.Ordinal);
        Assert.Contains("NoStore = true", controller, StringComparison.Ordinal);
        Assert.Contains("Provider track ID (optional)", script, StringComparison.Ordinal);
        Assert.Contains("Rotating corpus", script, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
