namespace allstarr.Tests;

public sealed class WebUiIntelligenceContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void IntelligenceView_UsesBackedApiStatesPrivacyExplanationsAndGeneratedActions()
    {
        var script = File.ReadAllText(Path.Combine(Root, "allstarr", "wwwroot", "js", "webui.js"));
        var controller = File.ReadAllText(Path.Combine(Root, "allstarr", "Controllers", "IntelligenceController.cs"));
        foreach (var token in new[] { "/api/admin/intelligence", "empty", "loading", "configured", "disabled",
                     "degraded", "unauthorized", "error", "retentionDays", "allowedSignalTypes", "enabledProviders",
                     "Why this track", "Generated playlists", "Create preview" })
            Assert.Contains(token, script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("crypto.randomUUID", script, StringComparison.Ordinal);
        Assert.Contains("MusicBrainz-enriched genres, credits, and relationships", controller, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MusicBrainz is metadata, not a personalized recommendation account", controller, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntelligenceView_HasKeyboardAndNarrowScreenContracts()
    {
        var script = File.ReadAllText(Path.Combine(Root, "allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(Path.Combine(Root, "allstarr", "wwwroot", "css", "base.css"));
        Assert.Contains("tabindex=\"0\"", script, StringComparison.Ordinal);
        Assert.Contains("<details><summary>", script, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", script, StringComparison.Ordinal);
        Assert.Contains(".intelligence-results .activity-item:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 960px)", css, StringComparison.Ordinal);
        Assert.Contains(".intelligence-bars meter", css, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current != null && !File.Exists(Path.Combine(current, "allstarr.sln"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
