namespace allstarr.Tests;

public sealed class FreshInstallContractTests
{
    private readonly string _repositoryRoot = FindRepositoryRoot();

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
            "Models",
            "Settings",
            "SpotifyImportSettings.cs"));

        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_IDS", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_NAMES", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistLocalTracksPositions", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleEnvironment_DescribesTheFreshPostgresDeployment()
    {
        var example = File.ReadAllText(Path.Combine(_repositoryRoot, ".env.example"));

        Assert.Contains("POSTGRES_PASSWORD_FILE=./secrets/postgres-password.txt", example, StringComparison.Ordinal);
        Assert.Contains("ALLSTARR_KEYRING_FILE=./secrets/allstarr-keyring.json", example, StringComparison.Ordinal);
        Assert.DoesNotContain("VALKEY_MAX_MEMORY", example, StringComparison.Ordinal);
        Assert.DoesNotContain("REDIS_ENABLED", example, StringComparison.Ordinal);
        Assert.Contains("BACKEND_TYPE=Jellyfin", example, StringComparison.Ordinal);
        Assert.DoesNotContain("SUBSONIC_URL=", example, StringComparison.Ordinal);
        Assert.DoesNotContain("JELLYFIN_URL=", example, StringComparison.Ordinal);
        Assert.DoesNotContain("JELLYFIN_API_KEY=", example, StringComparison.Ordinal);
        Assert.Contains("KEPT_PATH=./kept", example, StringComparison.Ordinal);
        Assert.DoesNotContain("REDIS_DATA_PATH", example, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_IDS", example, StringComparison.Ordinal);
        Assert.DoesNotContain("SPOTIFY_IMPORT_PLAYLIST_NAMES", example, StringComparison.Ordinal);
        Assert.DoesNotContain("automatically started in docker-compose", example, StringComparison.OrdinalIgnoreCase);
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
