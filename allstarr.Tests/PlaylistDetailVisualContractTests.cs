namespace allstarr.Tests;

public sealed class PlaylistDetailVisualContractTests
{
    private readonly string _script = Read("allstarr/wwwroot/js/webui.js");
    private readonly string _styles = Read("allstarr/wwwroot/css/workspaces.css");

    [Fact]
    public void Summary_UsesTargetBrandingAndRatioDrivenPlayableColor()
    {
        Assert.Contains("this.renderProviderLogo(targetBackend, \"small\")", _script, StringComparison.Ordinal);
        Assert.Contains("const playableRatio =", _script, StringComparison.Ordinal);
        Assert.Contains("--playable-hue:", _script, StringComparison.Ordinal);
        Assert.Contains("playlist-playable-stat", _script, StringComparison.Ordinal);
        Assert.Contains("percent playable", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("hero-stat-icon\">${icon(\"library\")}", _script, StringComparison.Ordinal);
        Assert.Contains(".playlist-playable-stat .hero-stat-icon", _styles, StringComparison.Ordinal);
        Assert.Contains("linear-gradient(", _styles, StringComparison.Ordinal);
        Assert.Contains("var(--playable-color)", _styles, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
