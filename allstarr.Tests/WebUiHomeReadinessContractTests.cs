namespace allstarr.Tests;

public sealed class WebUiHomeReadinessContractTests
{
    private readonly string _script = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void HomeOffersOneGuidedCoreReadinessCheck()
    {
        Assert.Contains("Run check", _script, StringComparison.Ordinal);
        Assert.Contains("API.mediaProbe()", _script, StringComparison.Ordinal);
        Assert.Contains("API.playlistReadiness()", _script, StringComparison.Ordinal);
        Assert.Contains("Player artwork", _script, StringComparison.Ordinal);
        Assert.Contains("Restored playlists", _script, StringComparison.Ordinal);
        Assert.Contains("Playlist sources", _script, StringComparison.Ordinal);
        Assert.Contains("readyPlaylistProviderIds.length", _script, StringComparison.Ordinal);
        Assert.Contains("Playlist source", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("Spotify refresh", _script, StringComparison.Ordinal);
        Assert.Contains("Fix source connections", _script, StringComparison.Ordinal);
        Assert.Contains("Rematch with available providers", _script, StringComparison.Ordinal);
        Assert.Contains("affectedPlaylists.map((name) =>", _script, StringComparison.Ordinal);
        Assert.Contains("this.runManagedPlaylistByName(name, false)", _script, StringComparison.Ordinal);
        Assert.Contains("Review affected playlists", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeKeepsDestructiveMaintenanceOutOfPrimaryActions()
    {
        var start = _script.IndexOf("renderHome()", StringComparison.Ordinal);
        var end = _script.IndexOf("renderReadinessCheck", start, StringComparison.Ordinal);
        var home = _script[start..end];

        Assert.DoesNotContain("API.clearCache()", home, StringComparison.Ordinal);
        Assert.DoesNotContain("API.restart()", home, StringComparison.Ordinal);
    }
}
