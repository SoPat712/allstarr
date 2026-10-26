using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class FreshInstallContractTests
{
    private readonly string _repositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("Jellyfin", false)]
    [InlineData("Subsonic", false)]
    [InlineData("Jellyfin", true)]
    [InlineData("Subsonic", true)]
    public async Task FreshPlaybackDefaultsPublishAThreeDayCache(string backend, bool packagedConfiguration)
    {
        var root = Path.Combine(Path.GetTempPath(), $"playback-cache-defaults-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var builder = new ConfigurationBuilder();
            if (packagedConfiguration) builder.AddJsonFile(Path.Combine(_repositoryRoot, "allstarr", "appsettings.json"));
            var configuration = builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = root
            }).Build();
            MediaBackendSettings settings = backend == "Jellyfin"
                ? packagedConfiguration ? configuration.GetSection(backend).Get<JellyfinSettings>()! : new JellyfinSettings()
                : packagedConfiguration ? configuration.GetSection(backend).Get<SubsonicSettings>()! : new SubsonicSettings();
            string? published = null;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(),
                    It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) => published = path)
                .Returns(Task.CompletedTask);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "track"))
                .ReturnsAsync(() => published);
            await using var cache = new ManagedTrackCacheService(configuration,
                Options.Create(new SubsonicSettings { StorageMode = settings.StorageMode, CacheDurationHours = settings.CacheDurationHours }),
                local.Object, NullLogger<ManagedTrackCacheService>.Instance);
            var audio = "fLaCfixture-audio"u8.ToArray();
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
            var stream = new ProtocolProviderStream(response,
                new("fixture", new Uri("https://media.example.test/track"), DateTimeOffset.UtcNow.AddMinutes(1),
                    true, true, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "track");
            var context = new ProtocolExecutionContext(
                backend == "Jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic, "fixture", "principal",
                new AllstarrPrincipal(Guid.CreateVersion7(), backend.ToLowerInvariant(), "fixture", "principal", "Listener", false),
                "cache-defaults", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
            await cache.WrapAsync(stream, context, "deezer", "track", false,
                () => Task.FromResult<Song?>(new Song { Title = "Track", Artist = "Artist" }), default);
            Assert.Equal(audio, await response.Content.ReadAsByteArrayAsync());
            var replay = await cache.TryOpenAsync(new("deezer", ProviderResourceKind.Track, "track"),
                new(null, ProviderAudioQuality.Any), default);
            Assert.NotNull(replay);
            using var cachedResponse = replay.Response;
            Assert.True(replay.IsCached);
            Assert.Equal(audio, await cachedResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal(72, settings.CacheDurationHours);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Startup_DoesNotRegisterOrRunLegacyStateMigrations()
    {
        var program = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr", "Program.cs"));

        Assert.DoesNotContain("FavoritesMigrationService", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Services.Common.EnvMigrationService", program, StringComparison.Ordinal);
        Assert.Contains("Core.Configuration.LegacyEnvMigrationService", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddHostedService<allstarr.Core.Configuration.LegacyEnvMigrationService",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SpotifyMappingMigrationService", program, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionUpgradeRebuildService", program, StringComparison.Ordinal);
        Assert.DoesNotContain("SpotifyImport:PlaylistIds", program, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Spotify",
            "SpotifyMappingMigrationService.cs")));
        Assert.False(File.Exists(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Common",
            "EnvMigrationService.cs")));
        Assert.False(File.Exists(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Common",
            "FavoritesMigrationService.cs")));
    }

    [Fact]
    public void RuntimeEnvironment_DoesNotRecognizeRetiredLegacyPlaylistVariables()
    {
        var configuration = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Services",
            "Common",
            "RuntimeEnvConfiguration.cs"));
        var settings = File.ReadAllText(Path.Combine(
            _repositoryRoot,
            "allstarr",
            "Providers",
            "Spotify",
            "SpotifyImportSettings.cs"));

        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_IDS", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_NAMES", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistLocalTracksPositions", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleEnvironment_DescribesTheFreshSqliteDeployment()
    {
        var example = File.ReadAllText(Path.Combine(_repositoryRoot, ".env.example"));

        Assert.DoesNotContain("POSTGRES_", example, StringComparison.Ordinal);
        Assert.DoesNotContain("ALLSTARR_KEYRING_FILE", example, StringComparison.Ordinal);
        Assert.DoesNotContain("VALKEY_MAX_MEMORY", example, StringComparison.Ordinal);
        Assert.DoesNotContain("REDIS_ENABLED", example, StringComparison.Ordinal);
        Assert.Contains("BACKEND_TYPE=Jellyfin", example, StringComparison.Ordinal);
        Assert.Contains("SUBSONIC_URL=", example, StringComparison.Ordinal);
        Assert.Contains("JELLYFIN_URL=", example, StringComparison.Ordinal);
        Assert.DoesNotContain("JELLYFIN_API_KEY=", example, StringComparison.Ordinal);
        Assert.Contains("ALLSTARR_DATA_PATH=./data", example, StringComparison.Ordinal);
        Assert.DoesNotContain("REDIS_DATA_PATH", example, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_IDS", example, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_NAMES", example, StringComparison.Ordinal);
        Assert.DoesNotContain("automatically started in docker-compose", example, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Architecture_PublishesAndEnforcesTheStateOwnershipMatrix()
    {
        var architecture = File.ReadAllText(Path.Combine(
            _repositoryRoot, "docs", "architecture", "overview.md"));
        foreach (var owner in new[] { "SQLite", "Filesystem", "Environment / deployment secrets" })
        {
            Assert.Contains($"| {owner} |", architecture, StringComparison.Ordinal);
        }
        Assert.Contains("Legacy `.env` input is accepted only through the explicit preview/apply migration boundary", architecture, StringComparison.Ordinal);

        var dbContext = string.Join('\n',
            Directory.GetFiles(
                    Path.Combine(_repositoryRoot, "allstarr", "Core", "Storage"),
                    "AllstarrDbContext*.cs")
                .Select(File.ReadAllText));
        foreach (var durableEntity in new[]
                 {
                     "AdminAuthSession", "ProviderAccount", "RuntimeSetting",
                     "PlaylistLink", "PlaylistSourceSnapshot", "PlaylistSyncRun",
                     "TrackMatch", "DurableJob",
                     "AuditEvent", "ExtensionPackage"
                 })
        {
            Assert.Contains($"DbSet<{durableEntity}", dbContext, StringComparison.Ordinal);
        }

        var runtimeSource = string.Join('\n',
            Directory.GetFiles(
                    Path.Combine(_repositoryRoot, "allstarr"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        foreach (var removedAuthority in new[]
                 {
                     "mappings.json", "sessions.protected", "endpoint-usage.csv",
                     "missing_tracks.json", "_spotify.json"
                 })
        {
            Assert.DoesNotContain(removedAuthority, runtimeSource, StringComparison.OrdinalIgnoreCase);
        }

        var program = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr", "Program.cs"));
        foreach (var removedMatcher in new[]
                 {
                     "SpotifyPlaylistMatchingAdapter", "PlaylistMatchingCoordinator",
                     "IPlaylistMatchingCoordinator", "playlist.match-all"
                 })
        {
            Assert.DoesNotContain(removedMatcher, program, StringComparison.Ordinal);
            Assert.DoesNotContain(removedMatcher, runtimeSource, StringComparison.Ordinal);
        }
        foreach (var removedCompatibilityPath in new[]
                 {
                     "SpotifyApiClient", "InjectedPlaylistItemHelper",
                     "SpotifyPlaylistCountHelper", "mappings/tracks",
                     "playlists/{name}/map", "jellyfin/search",
                     "external/search", "spotify/user-playlists"
                 })
        {
            Assert.DoesNotContain(removedCompatibilityPath, runtimeSource, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("PlaylistMaterializationJobHandler", runtimeSource, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate allstarr.sln");
    }
}
