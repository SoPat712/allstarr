namespace allstarr.Tests;

public sealed class PlaylistSourceDiscoveryWebUiContractTests
{
    private readonly string _script = ReadRepositoryFile("allstarr", "wwwroot", "js", "webui.js");

    [Fact]
    public void PlaylistDiscoverySeparatesProviderFailuresFromEmptyAccounts()
    {
        Assert.Contains("sourceAccountId: accountId, sourcePlaylist: null, sourceQuery: \"\", sourceNextCursor: \"\", loading: true, error: \"\"",
            _script, StringComparison.Ordinal);
        Assert.Contains("if (!items.length && this.playlistWizard.error) return nothing;",
            _script, StringComparison.Ordinal);
        Assert.Contains("No playlists found for this account. Try a search.",
            _script, StringComparison.Ordinal);
        Assert.DoesNotContain("No playlists found. Try a search or choose another account.",
            _script, StringComparison.Ordinal);
        Assert.Contains("error.retryAfterSeconds = details.retryAfterSeconds;",
            _script, StringComparison.Ordinal);
        Assert.Contains("setPlaylistSourceFailure(error)",
            _script, StringComparison.Ordinal);
        Assert.Contains("Spotify is temporarily limiting playlist requests", _script.Replace(
            "`${provider} is temporarily limiting playlist requests.`",
            "Spotify is temporarily limiting playlist requests"), StringComparison.Ordinal);
        Assert.Contains("Retry playlist browsing", _script, StringComparison.Ordinal);
        Assert.Contains("Math.min(900, requestedDelay)", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPlaylist_UsesOneProviderAwareModalWizard()
    {
        Assert.Contains("if (!this.playlistWizardOpen) return nothing;", _script, StringComparison.Ordinal);
        Assert.Contains("class=\"modal-backdrop playlist-wizard-backdrop\"", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\" aria-modal=\"true\" aria-labelledby=\"playlist-wizard-title\"", _script,
            StringComparison.Ordinal);
        Assert.Contains("playlistWizardHasInput()", _script, StringComparison.Ordinal);
        Assert.Contains("Discard this playlist setup?", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("renderInjectedAddModal", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("<label>Spotify ID</label>", _script, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var relativePath = Path.Combine(segments);
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            relativePath));
        return File.ReadAllText(path);
    }
}
