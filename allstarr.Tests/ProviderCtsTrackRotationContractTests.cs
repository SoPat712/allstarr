namespace allstarr.Tests;

public sealed class ProviderCtsTrackRotationContractTests
{
    [Fact]
    public void Selector_UsesABoundedProviderSpecificCorpus()
    {
        var selector = Read("allstarr/Services/Common/ProviderCtsTrackSelector.cs");

        Assert.Contains("public const int CorpusLimit = 100", selector, StringComparison.Ordinal);
        Assert.Contains("snapshot.ProviderAccountId == providerAccountId", selector, StringComparison.Ordinal);
        Assert.Contains("snapshot.ProviderId == providerId", selector, StringComparison.Ordinal);
        Assert.Contains("identity.ProviderId == providerId", selector, StringComparison.Ordinal);
        Assert.Contains("identity.ResourceKind == ProviderResourceKind.Track", selector, StringComparison.Ordinal);
        Assert.Contains("(current + 1) % corpus.Length", selector, StringComparison.Ordinal);
        Assert.Contains("snapshot.ProviderTrackIdentityId equals identity.Id", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_AutomaticallySelectsATrackButKeepsManualOverride()
    {
        var controller = Read("allstarr/Controllers/ProviderDiagnosticsController.cs");
        var runner = Read("allstarr/Services/Common/ProviderCtsDiagnosticRunner.cs");
        var script = Read("allstarr/wwwroot/js/webui.js");

        Assert.Contains("trackSelector.SelectAsync", runner, StringComparison.Ordinal);
        Assert.Contains("automaticTrack == null ? \"manual\" : \"rotating-corpus\"", runner, StringComparison.Ordinal);
        Assert.Contains("public string? TrackId", controller, StringComparison.Ordinal);
        Assert.Contains("NoCache = true", runner, StringComparison.Ordinal);
        Assert.Contains("NoStore = true", runner, StringComparison.Ordinal);
        Assert.Contains("Provider track ID (optional)", script, StringComparison.Ordinal);
        Assert.Contains("Rotating corpus", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Measurements_AreDurableAndReloadedIntoSources()
    {
        var controller = Read("allstarr/Controllers/ProviderDiagnosticsController.cs");
        var runner = Read("allstarr/Services/Common/ProviderCtsDiagnosticRunner.cs");
        var script = Read("allstarr/wwwroot/js/webui.js");

        Assert.Contains("healthStore.RecordAsync", runner, StringComparison.Ordinal);
        Assert.Contains("\"click-to-stream\"", runner, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"deep-stream/latest\")]", controller, StringComparison.Ordinal);
        Assert.Contains("API.ctsMeasurements()", script, StringComparison.Ordinal);
        Assert.Contains("class=\"cts-persisted\"", script, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
