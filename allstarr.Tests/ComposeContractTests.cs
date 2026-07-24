using System.Diagnostics;
using System.Text.RegularExpressions;

namespace allstarr.Tests;

public sealed class ComposeContractTests
{
    private readonly string _repositoryRoot = FindRepositoryRoot();

    [Fact]
    public void StandardCompose_IsCorePostgresAndUsesPinnedInfrastructureImages()
    {
        var path = Path.Combine(_repositoryRoot, "docker-compose.yml");
        var compose = File.ReadAllText(path);

        Assert.Contains("postgres:18.4-alpine3.23@sha256:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("valkey", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redis", compose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Storage__Provider: Postgres", compose, StringComparison.Ordinal);
        Assert.Contains("Storage__PasswordFile: /run/secrets/postgres_password", compose, StringComparison.Ordinal);
        Assert.Contains("Secrets__KeyRingPath: /run/secrets/allstarr_keyring", compose, StringComparison.Ordinal);
        Assert.Contains("${ADMIN_BIND_ADDRESS:-127.0.0.1}:${ADMIN_PORT:-5275}:5275", compose, StringComparison.Ordinal);
        Assert.Contains("Admin__Containerized: \"true\"", compose, StringComparison.Ordinal);
        Assert.Contains("Admin__ContainerGateway: auto", compose, StringComparison.Ordinal);
        Assert.Contains("/health/ready", compose, StringComparison.Ordinal);
        Assert.Contains("postgres-data:/var/lib/postgresql", compose, StringComparison.Ordinal);
        Assert.Contains("allstarr-state:/app/state", compose, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/sopat712/allstarr:3.1.0-beta.1", compose, StringComparison.Ordinal);
        Assert.Contains("Providers__MetadataFanoutConcurrency: ${PROVIDER_METADATA_FANOUT_CONCURRENCY:-4}", compose, StringComparison.Ordinal);
        Assert.Contains("Operations__EventLog__MaximumRows: ${EVENT_LOG_MAXIMUM_ROWS:-250000}", compose, StringComparison.Ordinal);
        Assert.Contains("Subsonic__EnableExternalPlaylists: ${ENABLE_EXTERNAL_PLAYLISTS:-false}", compose, StringComparison.Ordinal);
        Assert.Contains("Jellyfin__EnableExternalPlaylists: ${ENABLE_EXTERNAL_PLAYLISTS:-false}", compose, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/run/docker.sock", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("  gamdl-aio:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("  spotify-lyrics:", compose, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_repositoryRoot, "docker-compose-redis2valkey.yml")));
        Assert.DoesNotContain("REDIS_DATA_PATH", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentExample_UsesCanonicalBetaAndSafePlaylistDefaults()
    {
        var environment = File.ReadAllText(Path.Combine(_repositoryRoot, ".env.example"));
        var versionSource = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr", "AppVersion.cs"));
        var version = Regex.Match(versionSource, "Version\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains($"ALLSTARR_IMAGE=ghcr.io/sopat712/allstarr:{version}", environment, StringComparison.Ordinal);
        Assert.Contains("PROVIDER_METADATA_FANOUT_CONCURRENCY=4", environment, StringComparison.Ordinal);
        Assert.Contains("EVENT_LOG_MAXIMUM_ROWS=250000", environment, StringComparison.Ordinal);
        Assert.Contains("ENABLE_EXTERNAL_PLAYLISTS=false", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("Link Playlists tab", environment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerContext_ExcludesSecretsMediaSessionsAndResearchTrees()
    {
        var ignore = File.ReadAllText(Path.Combine(_repositoryRoot, ".dockerignore"));

        Assert.Contains("secrets/", ignore, StringComparison.Ordinal);
        Assert.Contains(".env", ignore, StringComparison.Ordinal);
        Assert.Contains("downloads/", ignore, StringComparison.Ordinal);
        Assert.Contains("kept/", ignore, StringComparison.Ordinal);
        Assert.Contains("apis/", ignore, StringComparison.Ordinal);
        Assert.Contains("first-party/", ignore, StringComparison.Ordinal);
        Assert.Contains("tools/", ignore, StringComparison.Ordinal);
        Assert.Contains("**/bin/", ignore, StringComparison.Ordinal);
        Assert.Contains("**/obj/", ignore, StringComparison.Ordinal);
    }

    [Fact]
    public void StandardCompose_RendersSuccessfully()
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
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--quiet");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start docker compose validation.");
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();

        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public void OptionalComposeFiles_RenderAndPreserveExplicitDeploymentBoundaries()
    {
        RenderCompose("docker-compose.yml", "docker-compose.dev.yml");
        RenderCompose("docker-compose.yml", "docker-compose.aio.yml");

        var aio = File.ReadAllText(Path.Combine(_repositoryRoot, "docker-compose.aio.yml"));
        Assert.DoesNotContain("gamdl-aio:", aio, StringComparison.Ordinal);
        Assert.DoesNotContain("AppleMusic__BaseUrl", aio, StringComparison.Ordinal);
        Assert.Contains("./first-party/dist:/app/first-party-bundle:ro", aio, StringComparison.Ordinal);
        Assert.Contains("Extensions__FirstPartyBundleLockPath:", aio, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", aio, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/run/docker.sock", aio, StringComparison.Ordinal);

    }

    [Fact]
    public void SpotifyLyricsOverlay_IsOptionalPinnedAndPrivate()
    {
        RenderCompose("docker-compose.yml", "docker-compose.spotify-lyrics.yml");
        var overlay = File.ReadAllText(Path.Combine(_repositoryRoot, "docker-compose.spotify-lyrics.yml"));

        Assert.Contains("akashrchandran/spotify-lyrics-api@sha256:", overlay, StringComparison.Ordinal);
        Assert.Contains("  spotify-lyrics:", overlay, StringComparison.Ordinal);
        Assert.Contains("SP_DC: ${SPOTIFY_API_SESSION_COOKIE:-}", overlay, StringComparison.Ordinal);
        Assert.Contains("SpotifyApi__LyricsApiUrl:", overlay, StringComparison.Ordinal);
        Assert.Contains("healthcheck:", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", overlay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/run/docker.sock", overlay, StringComparison.Ordinal);

        var controller = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr.sh"));
        Assert.Contains("enable spotify-lyrics|aio", controller, StringComparison.Ordinal);
        Assert.Contains("spotify|spotify-lyrics", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleOverlay_IsOptionalSourceLockedAndPrivate()
    {
        RenderCompose("docker-compose.yml", "docker-compose.apple.yml");
        var overlay = File.ReadAllText(Path.Combine(_repositoryRoot, "docker-compose.apple.yml"));
        var gatewayDockerfile = File.ReadAllText(Path.Combine(
            _repositoryRoot, "sidecars", "apple-gateway", "Dockerfile"));
        var gatewayProject = File.ReadAllText(Path.Combine(
            _repositoryRoot, "sidecars", "apple-gateway", "pyproject.toml"));
        var sourceLock = File.ReadAllText(Path.Combine(
            _repositoryRoot, "tools", "apple-provider", "source-lock.json"));

        Assert.Contains("AppleDownload__BaseUrl: http://apple-gateway:8000", overlay, StringComparison.Ordinal);
        Assert.Contains("APPLE_GATEWAY_WRAPPER_DECRYPT_PORT: 10020", overlay, StringComparison.Ordinal);
        Assert.Contains("./.apple-provider/wrapper-v2", overlay, StringComparison.Ordinal);
        Assert.Contains("WRAPPER_RESTORE_SESSION: \"1\"", overlay, StringComparison.Ordinal);
        Assert.Contains(
            "apple-wrapper-app-data:/app/rootfs/data/data/com.apple.android.music",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains("apple-wrapper-session:", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/run/docker.sock", overlay, StringComparison.Ordinal);
        Assert.Contains("python:3.12.11-slim-bookworm@sha256:", gatewayDockerfile, StringComparison.Ordinal);
        Assert.Contains("gamdl==3.8.2", gatewayProject, StringComparison.Ordinal);
        Assert.Contains("0bc16acb55f557b5c98d49f21d7af685410f7f8b", sourceLock, StringComparison.Ordinal);

        var controller = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr.sh"));
        Assert.Contains("prepare-apple) prepare_apple \"$@\" ;;", controller, StringComparison.Ordinal);
        Assert.Contains("case \"$(uname -m)\" in", controller, StringComparison.Ordinal);
        Assert.Contains("x86_64|amd64) arch=\"x86_64\"", controller, StringComparison.Ordinal);
        Assert.Contains("arm64|aarch64) arch=\"arm64-v8a\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("arch=\"${2:-x86_64}\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("down -v", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceUpdate_FastForwardsTrackedBranchButUpNeverPullsSource()
    {
        var controller = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr.sh"));

        Assert.Contains("git diff --quiet && git diff --cached --quiet", controller, StringComparison.Ordinal);
        Assert.Contains("git pull --ff-only", controller, StringComparison.Ordinal);
        Assert.Contains("tracked source files have local changes", controller, StringComparison.Ordinal);
        var upBody = controller[controller.IndexOf("up() {", StringComparison.Ordinal)..controller.IndexOf("update() {", StringComparison.Ordinal)];
        Assert.DoesNotContain("git pull", upBody, StringComparison.Ordinal);
        Assert.DoesNotContain("down -v", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Upgrade_CreatesPortableStateExportBeforeUpdating()
    {
        var controller = File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr.sh"));

        Assert.Contains("upgrade [OUTPUT_DIR]", controller, StringComparison.Ordinal);
        Assert.Contains("backup [OUTPUT_DIR]", controller, StringComparison.Ordinal);
        Assert.Contains("volume-data.tar.gz", controller, StringComparison.Ordinal);
        Assert.Contains("deployment-files.tar", controller, StringComparison.Ordinal);
        Assert.Contains("allstarr_allstarr-cache:/volume-cache:ro", controller, StringComparison.Ordinal);
        Assert.Contains("allstarr_postgres-data:/volume-postgres:ro", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("allstarr_valkey-data", controller, StringComparison.Ordinal);
        Assert.Contains("backup_state \"${1:-$ROOT/allstarr-backups}\" false", controller, StringComparison.Ordinal);
        Assert.Contains("restore BACKUP --confirm-replace", controller, StringComparison.Ordinal);
        Assert.Contains("validate_restore_archive", controller, StringComparison.Ordinal);
        Assert.Contains("Creating a rollback backup of the current installation", controller, StringComparison.Ordinal);
        Assert.Contains("tar -czf /export/volume-data.tar.gz -C / $ARCHIVE_PATHS", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("down -v", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeImage_ContainsBackupToolsAndPinnedDotnetBases()
    {
        var dockerfile = File.ReadAllText(Path.Combine(_repositoryRoot, "Dockerfile"));

        Assert.Contains("sdk:10.0.301@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("aspnet:10.0.9@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("postgresql-client-18", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("postgresql-client ", dockerfile, StringComparison.Ordinal);
        Assert.Contains("/app/state/backups", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegration_ExercisesPostgres18AndComposeContracts()
    {
        var workflow = File.ReadAllText(Path.Combine(_repositoryRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("DOTNET_VERSION: \"10.0.301\"", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "postgres:18.4-alpine3.23@sha256:996d0920e4ff9df1fc19dacb904492f3c1ec0ec1cc338f0ad7123be7731c5f5e",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("ALLSTARR_TEST_POSTGRES:", workflow, StringComparison.Ordinal);
        Assert.Contains("postgresql-client-18", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet format allstarr.sln --no-restore --verify-no-changes --verbosity minimal",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("dotnet test --configuration Release --no-build", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml config --quiet", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.dev.yml config --quiet",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.aio.yml config --quiet",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.spotify-lyrics.yml config --quiet",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.apple.yml config --quiet",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("uv sync --frozen --extra test", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error: true", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ImagePublishing_CannotBypassTheNativeStorageAndSidecarGates()
    {
        var workflow = File.ReadAllText(Path.Combine(_repositoryRoot, ".github", "workflows", "docker.yml"));
        var publishJobIndex = workflow.IndexOf("\n  docker:\n", StringComparison.Ordinal);
        var dotnetTestIndex = workflow.IndexOf(
            "dotnet test --configuration Release --no-build",
            StringComparison.Ordinal);
        var smokeBuildIndex = workflow.IndexOf("name: Build smoke-test image", StringComparison.Ordinal);
        var smokeTestIndex = workflow.IndexOf("name: Smoke test built image", StringComparison.Ordinal);
        var imagePushIndex = workflow.IndexOf("id: publish", StringComparison.Ordinal);
        var digestVerificationIndex = workflow.IndexOf(
            "name: Verify published manifest digest",
            StringComparison.Ordinal);

        Assert.Contains("DOTNET_VERSION: \"10.0.301\"", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "postgres:18.4-alpine3.23@sha256:996d0920e4ff9df1fc19dacb904492f3c1ec0ec1cc338f0ad7123be7731c5f5e",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("ALLSTARR_TEST_POSTGRES:", workflow, StringComparison.Ordinal);
        Assert.Contains("postgresql-client-18", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet format allstarr.sln --no-restore --verify-no-changes --verbosity minimal",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("dotnet test --configuration Release --no-build", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml config --quiet", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.dev.yml config --quiet",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.aio.yml config --quiet",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.apple.yml config --quiet",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("needs: build-and-test", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "uses: docker/build-push-action@53b7df96c91f9c12dcc8a07bcb9ccacbed38856a # v7",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("curl --fail --silent http://127.0.0.1:8080/health/ready", workflow, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED_DIGEST: ${{ steps.publish.outputs.digest }}", workflow, StringComparison.Ordinal);
        Assert.Contains("docker buildx imagetools inspect", workflow, StringComparison.Ordinal);
        Assert.True(dotnetTestIndex >= 0 && publishJobIndex > dotnetTestIndex);
        Assert.True(smokeBuildIndex > publishJobIndex);
        Assert.True(smokeTestIndex > smokeBuildIndex);
        Assert.True(imagePushIndex > smokeTestIndex);
        Assert.True(digestVerificationIndex > imagePushIndex);
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


    private void RenderCompose(params string[] files)
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
        foreach (var file in files)
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(file);
        }
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--quiet");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start Docker Compose validation.");
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, error);
    }
}
