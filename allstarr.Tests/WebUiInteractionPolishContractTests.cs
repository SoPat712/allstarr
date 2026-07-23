namespace allstarr.Tests;

public sealed class WebUiInteractionPolishContractTests
{
    private readonly string _script = ReadRepositoryFile("allstarr", "wwwroot", "js", "webui.js");

    [Fact]
    public void DialogsRestoreFocusToTheirOpener()
    {
        Assert.Contains("this.dialogReturnFocus = document.activeElement instanceof HTMLElement", _script, StringComparison.Ordinal);
        Assert.Contains("returnFocus?.isConnected", _script, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame(() => returnFocus.focus())", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void OverflowMenusDismissWithEscapeAndRestoreTriggerFocus()
    {
        Assert.Contains("handleActionMenuKeydown(event)", _script, StringComparison.Ordinal);
        Assert.Contains("event.currentTarget.querySelector(\":scope > summary\")?.focus()", _script, StringComparison.Ordinal);
        Assert.True(CountOccurrences(_script, "this.handleActionMenuKeydown(event)") >= 2);
        Assert.Contains("const trigger = event.currentTarget.previousElementSibling", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"track-action-trigger\"", _script, StringComparison.Ordinal);
        Assert.Contains("aria-haspopup=\"menu\"", _script, StringComparison.Ordinal);
        Assert.Contains("${icon(\"moreVertical\", 18)}", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedArtworkLoadsWithoutBlockingRendering()
    {
        Assert.True(CountOccurrences(_script, "loading=\"lazy\" decoding=\"async\"") >= 7);
        Assert.Contains("class=\"art\" src=${coverArtUrl} alt=\"\" decoding=\"async\"", _script, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
