using System.Diagnostics;
using System.Text.RegularExpressions;

namespace allstarr.Tests;

public sealed class ComposeContractTests
{
    private readonly string _repositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Compose_IsPostgresOnlyWithExplicitOptionalProfiles()
    {
        var compose = File.ReadAllText(Path.Combine(_repositoryRoot, "docker-compose.yml"));

        Assert.Contains("postgres:18.4-alpine3.23@sha256:", compose, StringComparison.Ordinal);
        Assert.Contains("Storage__Provider: Postgres", compose, StringComparison.Ordinal);
        Assert.Contains("Storage__PasswordFile: \"/run/secrets/postgres_password\"", compose, StringComparison.Ordinal);
        Assert.Contains("Secrets__KeyRingPath: \"/run/secrets/allstarr_keyring\"", compose, StringComparison.Ordinal);
        Assert.Contains("${ADMIN_PORT:-5275}:5275", compose, StringComparison.Ordinal);
        Assert.Contains("/health/ready", compose, StringComparison.Ordinal);
        Assert.Contains("postgres-data:/var/lib/postgresql", compose, StringComparison.Ordinal);
        Assert.Contains("allstarr-state:/app/state", compose, StringComparison.Ordinal);
        Assert.Contains("./.env:/app/.env:ro", compose, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/sopat712/allstarr:3.1.0-beta.1", compose, StringComparison.Ordinal);
        Assert.Matches("(?s)spotify-lyrics:.*?profiles:\\s*- spotify-lyrics", compose);
        Assert.Matches("(?s)apple-gateway:.*?profiles:\\s*- apple", compose);
        Assert.Matches("(?s)apple-wrapper:.*?profiles:\\s*- apple", compose);
        Assert.Contains("akashrchandran/spotify-lyrics-api@sha256:", compose, StringComparison.Ordinal);
        Assert.Contains("SPOTIFY_API_SESSION_COOKIE", compose, StringComparison.Ordinal);
        Assert.Contains("APPLE_GATEWAY_WRAPPER_DECRYPT_PORT", compose, StringComparison.Ordinal);
        Assert.Contains("./.apple-provider/wrapper-v2", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("valkey", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redis", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":latest", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/run/docker.sock", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Jellyfin__ApiKey", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("SpotifyApi__SessionCookie", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Deezer__Arl", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Qobuz__UserAuthToken", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentExample_UsesCanonicalBeta()
    {
        var environment = File.ReadAllText(Path.Combine(_repositoryRoot, ".env.example"));
        var versionSource = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr", "AppVersion.cs"));
        var version = Regex.Match(versionSource, "Version\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains($"ALLSTARR_IMAGE=ghcr.io/sopat712/allstarr:{version}", environment, StringComparison.Ordinal);
        Assert.Contains("ADMIN_BIND_ANY_IP=false", environment, StringComparison.Ordinal);
        Assert.Contains("EXTENSIONS_ALLOW_REMOTE_INSTALL=false", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("PROVIDER_METADATA_FANOUT_CONCURRENCY", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("EVENT_LOG_MAXIMUM_ROWS", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("ENABLE_EXTERNAL_PLAYLISTS", environment, StringComparison.Ordinal);
        var keys = environment.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line[..line.IndexOf('=')])
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "ADMIN_BIND_ADDRESS",
            "ADMIN_BIND_ANY_IP",
            "ADMIN_PORT",
            "ADMIN_TRUSTED_SUBNETS",
            "ALLSTARR_IMAGE",
            "ALLSTARR_KEYRING_FILE",
            "APPLE_UPLOAD_PATH",
            "BACKEND_TYPE",
            "CORS_ALLOWED_ORIGINS",
            "CORS_ALLOW_CREDENTIALS",
            "DOWNLOAD_PATH",
            "EXTENSIONS_ALLOW_REMOTE_INSTALL",
            "KEPT_PATH",
            "POSTGRES_DB",
            "POSTGRES_PASSWORD_FILE",
            "POSTGRES_USER",
            "PROXY_BIND_ADDRESS",
            "PROXY_PORT",
            "SPOTIFY_API_SESSION_COOKIE"
        ], keys);
    }

    [Fact]
    public void DockerContext_ExcludesSecretsMediaAndResearch()
    {
        var ignore = File.ReadAllText(Path.Combine(_repositoryRoot, ".dockerignore"));

        Assert.Contains("secrets/", ignore, StringComparison.Ordinal);
        Assert.Contains(".env", ignore, StringComparison.Ordinal);
        Assert.Contains("downloads/", ignore, StringComparison.Ordinal);
        Assert.Contains("kept/", ignore, StringComparison.Ordinal);
        Assert.Contains("apis/", ignore, StringComparison.Ordinal);
        Assert.Contains("tools/", ignore, StringComparison.Ordinal);
        Assert.Contains("**/bin/", ignore, StringComparison.Ordinal);
        Assert.Contains("**/obj/", ignore, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData()]
    [InlineData("spotify-lyrics")]
    [InlineData("apple")]
    public void ComposeProfiles_RenderSuccessfully(params string[] profiles)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            WorkingDirectory = _repositoryRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("docker-compose.yml");
        foreach (var profile in profiles)
        {
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(profile);
        }
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--quiet");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start Docker Compose validation.");
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();

        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void Controller_UsesOneComposeFileAndFailsClosed()
    {
        var controller = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr.sh"));

        Assert.Contains("validate_deployment_files", controller, StringComparison.Ordinal);
        Assert.Contains("Invalid .env line", controller, StringComparison.Ordinal);
        Assert.Contains("Duplicate .env key", controller, StringComparison.Ordinal);
        Assert.Contains("--profile spotify-lyrics", controller, StringComparison.Ordinal);
        Assert.Contains("--profile apple", controller, StringComparison.Ordinal);
        Assert.Contains("git diff --quiet && git diff --cached --quiet", controller, StringComparison.Ordinal);
        Assert.Contains("git pull --ff-only", controller, StringComparison.Ordinal);
        Assert.Contains("tracked source files have local changes", controller, StringComparison.Ordinal);
        Assert.Contains("prepare-apple) prepare_apple \"$@\" ;;", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-compose.dev.yml", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-compose.aio.yml", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-compose.apple.yml", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("docker-compose.spotify-lyrics.yml", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("down -v", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Upgrade_PreservesPortableState()
    {
        var controller = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr.sh"));

        Assert.Contains("upgrade [OUTPUT_DIR]", controller, StringComparison.Ordinal);
        Assert.Contains("backup [OUTPUT_DIR]", controller, StringComparison.Ordinal);
        Assert.Contains("volume-data.tar.gz", controller, StringComparison.Ordinal);
        Assert.Contains("deployment-files.tar", controller, StringComparison.Ordinal);
        Assert.Contains("allstarr_allstarr-cache:/volume-cache:ro", controller, StringComparison.Ordinal);
        Assert.Contains("allstarr_postgres-data:/volume-postgres:ro", controller, StringComparison.Ordinal);
        Assert.Contains("restore BACKUP --confirm-replace", controller, StringComparison.Ordinal);
        Assert.Contains("validate_restore_archive", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("allstarr_valkey-data", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeImage_ContainsBackupToolsAndPinnedDotnetBases()
    {
        var dockerfile = File.ReadAllText(Path.Combine(_repositoryRoot, "Dockerfile"));

        Assert.Contains("sdk:10.0.301@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("aspnet:10.0.9@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("postgresql-client-18", dockerfile, StringComparison.Ordinal);
        Assert.Contains("/app/state/backups", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegration_ExercisesSingleComposeModel()
    {
        var workflow = File.ReadAllText(Path.Combine(_repositoryRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("DOTNET_VERSION: \"10.0.301\"", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet format allstarr.sln --no-restore --verify-no-changes --verbosity minimal", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test --configuration Release --no-build", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml config --quiet", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml --profile spotify-lyrics config --quiet", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml --profile apple config --quiet", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error: true", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ImagePublishing_CannotBypassBuildAndTest()
    {
        var workflow = File.ReadAllText(Path.Combine(_repositoryRoot, ".github", "workflows", "docker.yml"));
        var publishJobIndex = workflow.IndexOf("\n  docker:\n", StringComparison.Ordinal);
        var dotnetTestIndex = workflow.IndexOf("dotnet test --configuration Release --no-build", StringComparison.Ordinal);
        var smokeBuildIndex = workflow.IndexOf("name: Build smoke-test image", StringComparison.Ordinal);
        var smokeTestIndex = workflow.IndexOf("name: Smoke test built image", StringComparison.Ordinal);
        var imagePushIndex = workflow.IndexOf("id: publish", StringComparison.Ordinal);

        Assert.Contains("needs: build-and-test", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml config --quiet", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml --profile apple config --quiet", workflow, StringComparison.Ordinal);
        Assert.Contains("Storage__Provider=Postgres", workflow, StringComparison.Ordinal);
        Assert.Contains("Backend__Type=Jellyfin", workflow, StringComparison.Ordinal);
        Assert.Contains("allstarr-release-smoke-postgres", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Storage__Provider=Sqlite", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Redis__Enabled", workflow, StringComparison.Ordinal);
        Assert.True(dotnetTestIndex >= 0 && publishJobIndex > dotnetTestIndex);
        Assert.True(smokeBuildIndex > publishJobIndex);
        Assert.True(smokeTestIndex > smokeBuildIndex);
        Assert.True(imagePushIndex > smokeTestIndex);
        Assert.Equal(1, workflow.Split("push: true", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("continue-on-error: true", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflows_PinEveryThirdPartyActionToACommitSha()
    {
        foreach (var file in new[] { "ci.yml", "docker.yml" })
        {
            var workflow = File.ReadAllText(Path.Combine(_repositoryRoot, ".github", "workflows", file));
            var actionReferences = Regex.Matches(workflow, @"(?m)^\s*uses:\s*[^\s@]+@([^\s#]+)");
            Assert.NotEmpty(actionReferences);
            Assert.All(actionReferences.Cast<Match>(), match =>
                Assert.Matches("^[0-9a-f]{40}$", match.Groups[1].Value));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate allstarr.sln");
    }
}
