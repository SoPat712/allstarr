namespace allstarr.Tests;

public sealed class WebUiPriorityOrderingContractTests
{
    private readonly string _script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

    [Fact]
    public void ProviderPriority_UsesAccessibleDragAndDropWithoutDuplicateChips()
    {
        Assert.Contains("draggable=\"true\"", _script, StringComparison.Ordinal);
        Assert.Contains("@dragstart=", _script, StringComparison.Ordinal);
        Assert.Contains("@dragover=", _script, StringComparison.Ordinal);
        Assert.Contains("@drop=", _script, StringComparison.Ordinal);
        Assert.Contains("Alt + Up or Alt + Down", _script, StringComparison.Ordinal);
        Assert.Contains("handlePriorityKeydown", _script, StringComparison.Ordinal);
        Assert.Contains("reorderPriority", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("this.movePriority", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-enabled-list", _script, StringComparison.Ordinal);
        Assert.Contains("cursor: grab", _css, StringComparison.Ordinal);
        Assert.Contains(".priority-item.dragging", _css, StringComparison.Ordinal);
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
