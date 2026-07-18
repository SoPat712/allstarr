namespace allstarr.Tests;

public sealed class WebUiPlaylistReadinessContractTests
{
    private readonly string _script = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void SettingsOffersPrivacySafePlaylistReadinessTest()
    {
        Assert.Contains("/api/admin/playlist-readiness", _script, StringComparison.Ordinal);
        Assert.Contains("Test playlist readiness", _script, StringComparison.Ordinal);
        Assert.Contains("does not reveal playlist or track names", _script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("playable ·", _script, StringComparison.Ordinal);
        Assert.Contains("unavailable", _script, StringComparison.Ordinal);
    }
}
