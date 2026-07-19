namespace allstarr.Tests;

public sealed class WebUiIconContractTests
{
    private readonly string _icons = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "allstarr", "wwwroot", "js", "ui", "icons.js"));

    [Fact]
    public void IconPathsUseTheSvgTemplateNamespace()
    {
        Assert.Contains("nothing, svg", _icons, StringComparison.Ordinal);
        Assert.Contains("home: svg`", _icons, StringComparison.Ordinal);
        Assert.Contains("shield: svg`", _icons, StringComparison.Ordinal);
        Assert.DoesNotContain("home: html`", _icons, StringComparison.Ordinal);
    }
}
