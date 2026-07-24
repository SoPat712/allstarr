namespace allstarr.Tests;

public sealed class TrackOverflowMenuWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

    [Fact]
    public void OverflowTrigger_IsCenteredAndPopoverIsNotClippedByTheRow()
    {
        Assert.Contains(".track-action-trigger {\n    display: inline-grid;\n    place-items: center;", _css, StringComparison.Ordinal);
        Assert.Contains("width: 40px;", _css, StringComparison.Ordinal);
        Assert.Contains("height: 40px;", _css, StringComparison.Ordinal);
        Assert.Contains(".playlist-track-row > .track-menu-cell {", _css, StringComparison.Ordinal);
        Assert.Contains("overflow: visible;", _css, StringComparison.Ordinal);
        Assert.Contains(".playlist-track-row:nth-last-child(-n + 3) .track-action-popover", _css, StringComparison.Ordinal);
    }

    [Fact]
    public void OverflowMenu_MovesFocusAndSupportsMenuKeyboardNavigation()
    {
        Assert.Contains("querySelector('[role=\"menuitem\"]:not(:disabled)')?.focus()", _script, StringComparison.Ordinal);
        Assert.Contains("[\"ArrowDown\", \"ArrowUp\", \"Home\", \"End\"]", _script, StringComparison.Ordinal);
        Assert.Contains("items[nextIndex]?.focus()", _script, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame(() => trigger?.focus())", _script, StringComparison.Ordinal);
        Assert.Contains("@click=${(event) => event.stopPropagation()}", _script, StringComparison.Ordinal);
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
