using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class RuntimeEnvConfigurationTests : IDisposable
{
    private readonly string _envFilePath = Path.Combine(
        Path.GetTempPath(),
        $"allstarr-runtime-{Guid.NewGuid():N}.env");

    [Fact]
    public void MapEnvVarToConfiguration_MapsFlatKeyToNestedConfigKey()
    {
        var mappings = RuntimeEnvConfiguration
            .MapEnvVarToConfiguration("SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS", "7")
            .ToList();

        var mapping = Assert.Single(mappings);
        Assert.Equal("SpotifyImport:MatchingIntervalHours", mapping.Key);
        Assert.Equal("7", mapping.Value);
    }

    [Fact]
    public void MapEnvVarToConfiguration_MapsSharedBackendKeysToBothSections()
    {
        var mappings = RuntimeEnvConfiguration
            .MapEnvVarToConfiguration("MUSIC_SERVICE", "Qobuz")
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, mappings.Count);
        Assert.Equal("Jellyfin:MusicService", mappings[0].Key);
        Assert.Equal("Qobuz", mappings[0].Value);
        Assert.Equal("Subsonic:MusicService", mappings[1].Key);
        Assert.Equal("Qobuz", mappings[1].Value);
    }

    [Fact]
    public void MapEnvVarToConfiguration_IgnoresComposeOnlyMountKeys()
    {
        var mappings = RuntimeEnvConfiguration
            .MapEnvVarToConfiguration("DOWNLOAD_PATH", "./downloads")
            .ToList();

        Assert.Empty(mappings);
    }

    [Theory]
    [InlineData("MULTI_PROVIDER_METADATA_ORDER")]
    [InlineData("MULTI_PROVIDER_DOWNLOAD_ORDER")]
    [InlineData("MULTI_PROVIDER_PLAYLIST_ORDER")]
    [InlineData("MULTI_PROVIDER_LYRICS_ORDER")]
    [InlineData("MULTI_PROVIDER_ENABLED_SEARCH")]
    [InlineData("MULTI_PROVIDER_ENABLED_PLAYLIST")]
    [InlineData("EXTENSION_REPOSITORIES")]
    public void MapEnvVarToConfiguration_MapsFlatPlatformKeys(string key)
    {
        var mappings = RuntimeEnvConfiguration
            .MapEnvVarToConfiguration(key, "value")
            .ToList();

        var mapping = Assert.Single(mappings);
        Assert.Equal(key, mapping.Key);
        Assert.Equal("value", mapping.Value);
    }

    [Fact]
    public void LoadDotEnvOverrides_StripsQuotesAndSupportsDoubleUnderscoreKeys()
    {
        File.WriteAllText(
            _envFilePath,
            """
            SPOTIFY_API_SESSION_COOKIE="secret-cookie"
            Admin__EnableEnvExport=true
            """);

        var overrides = RuntimeEnvConfiguration.LoadDotEnvOverrides(_envFilePath);

        Assert.Equal("secret-cookie", overrides["SpotifyApi:SessionCookie"]);
        Assert.Equal("true", overrides["Admin:EnableEnvExport"]);
    }

    [Fact]
    public void AddDotEnvOverrides_OverridesEarlierConfigurationValues()
    {
        File.WriteAllText(_envFilePath, "SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS=7\n");

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SpotifyImport:MatchingIntervalHours"] = "24"
        });

        RuntimeEnvConfiguration.AddDotEnvOverrides(configuration, _envFilePath);

        Assert.Equal(7, configuration.GetValue<int>("SpotifyImport:MatchingIntervalHours"));
    }

    public void Dispose()
    {
        if (File.Exists(_envFilePath))
        {
            File.Delete(_envFilePath);
        }
    }
}
