using System.Text.Json;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class ProviderIdsEnricherTests
{
    [Fact]
    public void EnsureSpotifyProviderIds_WhenProviderIdsMissing_AddsSpotifyKeys()
    {
        var item = new Dictionary<string, object?>
        {
            ["Name"] = "As the World Caves In",
            ["ProviderIds"] = null
        };

        ProviderIdsEnricher.EnsureSpotifyProviderIds(item, "2xXNLutYAOELYVObYb1C1S", "album-123");

        var providerIds = Assert.IsType<Dictionary<string, string>>(item["ProviderIds"]);
        Assert.Equal("2xXNLutYAOELYVObYb1C1S", providerIds["Spotify"]);
        Assert.Equal("album-123", providerIds["SpotifyAlbum"]);
    }

    [Fact]
    public void EnsureSpotifyProviderIds_WhenProviderIdsJsonElement_PreservesAndAdds()
    {
        using var doc = JsonDocument.Parse("""{"Jellyfin":"cde0216ad42ece9b66e2626a744e8283"}""");
        var item = new Dictionary<string, object?>
        {
            ["ProviderIds"] = doc.RootElement.Clone()
        };

        ProviderIdsEnricher.EnsureSpotifyProviderIds(item, "2xXNLutYAOELYVObYb1C1S", null);

        var providerIds = Assert.IsType<Dictionary<string, string>>(item["ProviderIds"]);
        Assert.Equal("cde0216ad42ece9b66e2626a744e8283", providerIds["Jellyfin"]);
        Assert.Equal("2xXNLutYAOELYVObYb1C1S", providerIds["Spotify"]);
    }

    [Fact]
    public void EnsureSpotifyProviderIds_WhenSpotifyAlreadyExists_DoesNotOverwrite()
    {
        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = "existing-spid"
        };
        var item = new Dictionary<string, object?>
        {
            ["ProviderIds"] = providerIds
        };

        ProviderIdsEnricher.EnsureSpotifyProviderIds(item, "new-spid", "album-1");

        var normalized = Assert.IsType<Dictionary<string, string>>(item["ProviderIds"]);
        Assert.Equal("existing-spid", normalized["Spotify"]);
        Assert.Equal("album-1", normalized["SpotifyAlbum"]);
    }
}
