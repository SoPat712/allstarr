namespace allstarr.Tests;

public sealed class WebUiIconContractTests
{
    private readonly string _icons = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "allstarr", "wwwroot", "js", "ui", "icons.js"));

    [Fact]
    public void IconsUseTheSameOriginSvgSprite()
    {
        Assert.Contains("/images/ui-icons.svg#", _icons, StringComparison.Ordinal);
        Assert.Contains("<use href=", _icons, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing, svg", _icons, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "allstarr", "wwwroot", "images", "ui-icons.svg")));
    }
}
