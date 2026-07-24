namespace allstarr.Tests;

public sealed class TrackMatchModalWebUiContractTests
{
    private readonly string _script = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(
        FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

    [Fact]
    public void TrackMatch_UsesSharedAccessibleModalInsteadOfInlineStickyEditor()
    {
        Assert.Contains("class=\"panel track-match-dialog redesigned-dialog\"", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\" aria-modal=\"true\"", _script, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"track-match-title\"", _script, StringComparison.Ordinal);
        Assert.Contains("this.handleDialogKeydown(event, close)", _script, StringComparison.Ordinal);
        Assert.Contains("if (event.target === event.currentTarget) close()", _script, StringComparison.Ordinal);
        var editorStart = _css.IndexOf(".track-match-editor {", StringComparison.Ordinal);
        var editorEnd = _css.IndexOf(".track-match-editor h4", editorStart, StringComparison.Ordinal);
        var editorCss = _css[editorStart..editorEnd];
        Assert.DoesNotContain("position: sticky", editorCss, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackMatch_PreservesSourceIdentityAndRestoresTriggerFocus()
    {
        Assert.Contains("editor.track?.albumArtUrl", _script, StringComparison.Ordinal);
        Assert.Contains("asArray(editor.track?.artists)", _script, StringComparison.Ordinal);
        Assert.Contains("this.trackMatchReturnFocus = returnFocus", _script, StringComparison.Ordinal);
        Assert.Contains("returnFocus?.isConnected", _script, StringComparison.Ordinal);
        Assert.Contains(".track-match-dialog", _script, StringComparison.Ordinal);
        Assert.Contains(".track-match-source {", _css, StringComparison.Ordinal);
        Assert.Contains("max-height: calc(100dvh - (2 * var(--space-3)));", _css, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackMatch_UsesSearchAndMatchActionsWithTabbedCandidateWorkspace()
    {
        Assert.Contains("this.openInjectedTrackEditor(track, \"local\"", _script, StringComparison.Ordinal);
        Assert.Contains(">Match</button>", _script, StringComparison.Ordinal);
        Assert.Contains("this.rematchInjectedTrack(track)", _script, StringComparison.Ordinal);
        Assert.Contains(">Search</button>", _script, StringComparison.Ordinal);
        Assert.Contains("<h3 id=\"track-match-title\">Match track</h3>", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\" aria-label=\"Match target\"", _script, StringComparison.Ordinal);
        Assert.Contains(">Local library</button>", _script, StringComparison.Ordinal);
        Assert.Contains(">Music providers</button>", _script, StringComparison.Ordinal);
        Assert.Contains("this.mappingReviewProviders({ providerIdentities: editor.track?.providerIdentities })", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"mapping-result-art\"", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"choose-label\">Choose", _script, StringComparison.Ordinal);
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
