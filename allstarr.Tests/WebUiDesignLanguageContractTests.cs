namespace allstarr.Tests;

public sealed class WebUiDesignLanguageContractTests
{
    private readonly string _script = ReadRepositoryFile("allstarr", "wwwroot", "js", "webui.js");
    private readonly string _foundation = ReadRepositoryFile("allstarr", "wwwroot", "css", "foundation.css");
    private readonly string _tokens = ReadRepositoryFile("allstarr", "wwwroot", "css", "tokens.css");
    private readonly string _engineering = ReadRepositoryFile("docs", "steering", "webui-engineering.md");
    private readonly string _design = ReadRepositoryFile("docs", "design", "webui-design-system.md");

    [Fact]
    public void PrimaryNavigation_UsesFiveDirectDestinations()
    {
        Assert.Contains("[\"home\", \"library\", \"sources\", \"activity\", \"settings\"]", _script, StringComparison.Ordinal);
        Assert.Contains("<div class=\"nav-section\">${primaryRoutes.map(renderNavLink)}</div>", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("const systemRoutes", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("<details class=\"nav-group\"", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedControls_OwnTypographyAndDimensions()
    {
        Assert.Contains("--control-height: 40px;", _tokens, StringComparison.Ordinal);
        Assert.Contains("--control-font-size: 0.875rem;", _tokens, StringComparison.Ordinal);
        Assert.Contains("--control-padding-inline: 12px;", _tokens, StringComparison.Ordinal);
        Assert.Contains("height: var(--control-height);", _foundation, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--control-font-size);", _foundation, StringComparison.Ordinal);
        Assert.Contains("padding-inline: var(--control-padding-inline);", _foundation, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignContracts_RequireSharedNavigationAndControls()
    {
        Assert.Contains("### App shell navigation", _engineering, StringComparison.Ordinal);
        Assert.Contains("### Form-control ownership", _engineering, StringComparison.Ordinal);
        Assert.Contains("### Form controls", _design, StringComparison.Ordinal);
        Assert.Contains("Home, Library, Sources, Event Log, and Settings", _engineering, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
