namespace allstarr.Tests;

public sealed class WebUiProviderRecoveryContractTests
{
    private readonly string _script = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "allstarr", "wwwroot", "js", "webui.js"));

    [Fact]
    public void DegradedSpotifyShowsSpecificRecoveryAction()
    {
        Assert.Contains("provider_unauthorized", _script, StringComparison.Ordinal);
        Assert.Contains("Reconnect Spotify", _script, StringComparison.Ordinal);
        Assert.Contains("fresh sp_dc cookie", _script, StringComparison.Ordinal);
        Assert.Contains("cached playlists will keep working", _script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Open setup", _script, StringComparison.Ordinal);
    }
}
