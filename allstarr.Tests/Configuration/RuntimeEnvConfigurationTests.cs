using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace allstarr.Tests;

[Collection(nameof(EnvironmentVariableCollection))]
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
    [InlineData("MULTI_PROVIDER_STREAMING_ORDER")]
    [InlineData("MULTI_PROVIDER_PLAYLIST_ORDER")]
    [InlineData("MULTI_PROVIDER_LYRICS_ORDER")]
    [InlineData("MULTI_PROVIDER_ENABLED_SEARCH")]
    [InlineData("MULTI_PROVIDER_ENABLED_PLAYLIST")]
    [InlineData("MULTI_PROVIDER_DISABLED_PROVIDERS")]
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

    [Fact]
    public void LoadDotEnvOverrides_DoesNotReplaceDeploymentOwnedAliasDestination()
    {
        File.WriteAllText(_envFilePath, "APPLE_DOWNLOAD_URL=http://legacy-gamdl:8000\n");

        var overrides = RuntimeEnvConfiguration.LoadDotEnvOverrides(
            _envFilePath,
            new HashSet<string>(["AppleDownload:BaseUrl"], StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("AppleDownload:BaseUrl", overrides);
    }

    [Fact]
    public void ResolveBackendSelection_reports_process_authority_and_dotenv_conflict()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-backend-{Guid.NewGuid():N}");
        var contentRoot = Path.Combine(root, "app");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(root, ".env"), "BACKEND_TYPE=Subsonic\n");
        var priorNested = Environment.GetEnvironmentVariable("Backend__Type");
        var priorFlat = Environment.GetEnvironmentVariable("BACKEND_TYPE");
        try
        {
            Environment.SetEnvironmentVariable("Backend__Type", "Jellyfin");
            Environment.SetEnvironmentVariable("BACKEND_TYPE", null);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backend:Type"] = "Jellyfin"
                })
                .Build();

            var selection = RuntimeEnvConfiguration.ResolveBackendSelection(
                configuration,
                new HostEnvironment(contentRoot));

            Assert.Equal("Jellyfin", selection.EffectiveValue);
            Assert.Equal("process-environment", selection.Source);
            Assert.True(selection.IsExplicitDeploymentValue);
            Assert.True(selection.HasConflictingDotEnvValue);
            Assert.Equal("Subsonic", selection.ConflictingDotEnvValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Backend__Type", priorNested);
            Environment.SetEnvironmentVariable("BACKEND_TYPE", priorFlat);
            Directory.Delete(root, recursive: true);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_envFilePath))
        {
            File.Delete(_envFilePath);
        }
    }

    private sealed class HostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "allstarr.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(contentRoot);
    }
}

[CollectionDefinition(nameof(EnvironmentVariableCollection), DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
