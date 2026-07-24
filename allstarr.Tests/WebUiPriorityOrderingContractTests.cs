namespace allstarr.Tests;

public sealed class WebUiPriorityOrderingContractTests
{
    private readonly string _script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));
    private readonly string _designSystemCss = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "css", "design-system.css"));
    private readonly string _controller = File.ReadAllText(
        FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));

    [Fact]
    public void ProviderPriority_UsesAccessibleDragAndDropWithoutDuplicateChips()
    {
        Assert.Contains("draggable=\"true\"", _script, StringComparison.Ordinal);
        Assert.Contains("@dragstart=", _script, StringComparison.Ordinal);
        Assert.Contains("@dragover=", _script, StringComparison.Ordinal);
        Assert.Contains("@drop=", _script, StringComparison.Ordinal);
        Assert.Contains("Alt + Up or Alt + Down", _script, StringComparison.Ordinal);
        Assert.Contains("handlePriorityKeydown", _script, StringComparison.Ordinal);
        Assert.Contains("position ${index + (group.pinnedProvider ? 2 : 1)}", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("position ${index + 2}", _script, StringComparison.Ordinal);
        Assert.Contains("reorderPriority", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("this.movePriority", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-enabled-list", _script, StringComparison.Ordinal);
        Assert.Contains("cursor: grab", _css, StringComparison.Ordinal);
        Assert.Contains(".priority-item.dragging", _css, StringComparison.Ordinal);
    }

    [Fact]
    public void PrioritySettings_ExposeLocalLyricsFirstAndUseConsistentSpacing()
    {
        var lyricsLabel = _controller.IndexOf("\"Lyrics priority\"", StringComparison.Ordinal);
        var lyricsGroupEnd = _controller.IndexOf("];", lyricsLabel, StringComparison.Ordinal);
        var lyricsGroup = _controller[lyricsLabel..lyricsGroupEnd];

        Assert.Contains("\"MULTI_PROVIDER_LYRICS_ORDER\"", lyricsGroup, StringComparison.Ordinal);
        Assert.Contains("pinnedProvider: pinnedLocalProvider", lyricsGroup, StringComparison.Ordinal);
        Assert.Contains(
            ".settings-routing .section-heading {\n    margin-bottom: var(--space-4);",
            _designSystemCss,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppleQualityOptions_AreHighestFirstWithFactualLabels()
    {
        const string orderedOptions =
            "[\"alac-24-192\", \"alac-24-96\", \"alac-24-48\", \"alac-16-44\"]";

        Assert.Contains(orderedOptions, _controller, StringComparison.Ordinal);
        Assert.Contains(
            "\"alac-24-96\": \"High-resolution · 24-bit / 96 kHz\"",
            _script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("one below maximum", _script, StringComparison.OrdinalIgnoreCase);
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
