using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Tests;

public sealed class ComposeContractTests
{
    private readonly string _repositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Compose_UsesOneDataFolderWithExplicitOptionalProfiles()
    {
        var compose = File.ReadAllText(Path.Combine(_repositoryRoot, "docker-compose.yml"));

        Assert.DoesNotContain("postgres", compose, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Storage__DataDirectory: \"/app/data\"", compose, StringComparison.Ordinal);
        Assert.Contains("Release__Profile: \"${ALLSTARR_RELEASE_PROFILE:-core}\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Storage__PasswordFile", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("Secrets__KeyRingPath", compose, StringComparison.Ordinal);
        Assert.Contains("${ADMIN_PORT:-5275}:5275", compose, StringComparison.Ordinal);
        Assert.Contains("/health/ready", compose, StringComparison.Ordinal);
        Assert.Contains("${ALLSTARR_DATA_PATH:-./data}:/app/data", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("./.env:/app/.env", compose, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/sopat712/allstarr:3.1.0-beta.1", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify-lyrics:", compose, StringComparison.Ordinal);
        Assert.Matches("(?s)apple-gateway:.*?profiles:\\s*- apple", compose);
        Assert.Matches("(?s)apple-wrapper:.*?profiles:\\s*- apple", compose);
        Assert.DoesNotContain("spotify-lyrics-api", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("SpotifyApi__LyricsApiUrl", compose, StringComparison.Ordinal);
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
            "ADMIN_BASE_PATH",
            "ADMIN_BIND_ADDRESS",
            "ADMIN_BIND_ANY_IP",
            "ADMIN_PORT",
            "ADMIN_TRUSTED_SUBNETS",
            "ALLSTARR_DATA_PATH",
            "ALLSTARR_IMAGE",
            "ALLSTARR_RELEASE_PROFILE",
            "BACKEND_TYPE",
            "JELLYFIN_URL",
            "PROXY_BIND_ADDRESS",
            "PROXY_PORT",
            "SUBSONIC_URL"
        ], keys);
    }

    [Fact]
    public void DockerContext_ExcludesSecretsMediaAndResearch()
    {
        var ignore = File.ReadAllText(Path.Combine(_repositoryRoot, ".dockerignore"));

        Assert.Contains("secrets/", ignore, StringComparison.Ordinal);
        Assert.Contains("data/", ignore, StringComparison.Ordinal);
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
        startInfo.ArgumentList.Add("--env-file");
        startInfo.ArgumentList.Add(Path.Combine(_repositoryRoot, ".env.example"));
        foreach (var profile in profiles)
        {
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(profile);
        }
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--services");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start Docker Compose validation.");
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();
        var services = process.StandardOutput.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var expected = new HashSet<string>(["allstarr"], StringComparer.Ordinal);
        if (profiles.Contains("apple")) expected.UnionWith(["apple-gateway", "apple-wrapper"]);

        Assert.True(process.ExitCode == 0, error);
        Assert.True(expected.SetEquals(services),
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", services)}].");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Compose_RendersOnePersistentCoreWithoutEnvironmentFile(bool apple, bool source)
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(Path.Combine(_repositoryRoot, "docker-compose.yml"), Path.Combine(root, "docker-compose.yml"));
            var start = new ProcessStartInfo("docker")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ALLSTARR_", StringComparison.Ordinal) ||
                         key.StartsWith("COMPOSE_", StringComparison.Ordinal) || key.StartsWith("ADMIN_", StringComparison.Ordinal) ||
                         key.StartsWith("PROXY_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
            start.ArgumentList.Add("compose");
            if (source)
            {
                File.Copy(Path.Combine(_repositoryRoot, "tools", "docker-compose.source.yml"), Path.Combine(root, "source.yml"));
                foreach (var argument in new[] { "-f", "docker-compose.yml", "-f", "source.yml" }) start.ArgumentList.Add(argument);
            }
            if (apple) { start.ArgumentList.Add("--profile"); start.ArgumentList.Add("apple"); }
            start.ArgumentList.Add("config");
            start.ArgumentList.Add("--format");
            start.ArgumentList.Add("json");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            using var model = JsonDocument.Parse(await output);
            var services = model.RootElement.GetProperty("services");
            Assert.Equal(apple ? 3 : 1, services.EnumerateObject().Count());
            var core = services.GetProperty("allstarr");
            Assert.Equal(source, core.TryGetProperty("build", out var build));
            if (source)
            {
                Assert.Equal(root, build.GetProperty("context").GetString());
                Assert.Equal("allstarr:development", core.GetProperty("image").GetString());
            }
            Assert.False(core.TryGetProperty("depends_on", out _));
            Assert.False(core.TryGetProperty("profiles", out _));
            var mount = Assert.Single(core.GetProperty("volumes").EnumerateArray());
            Assert.Equal("bind", mount.GetProperty("type").GetString());
            Assert.Equal(Path.Combine(root, "data"), mount.GetProperty("source").GetString());
            Assert.Equal("/app/data", mount.GetProperty("target").GetString());
            var ports = core.GetProperty("ports").EnumerateArray().ToArray();
            Assert.Equal(2, ports.Length);
            Assert.Contains(ports, port => port.GetProperty("target").GetInt32() == 8080 && port.GetProperty("published").GetString() == "5274");
            Assert.Contains(ports, port => port.GetProperty("target").GetInt32() == 5275 && port.GetProperty("host_ip").GetString() == "127.0.0.1");
            if (apple)
            {
                foreach (var name in new[] { "apple-gateway", "apple-wrapper" })
                {
                    var sidecar = services.GetProperty(name);
                    Assert.Equal("apple", Assert.Single(sidecar.GetProperty("profiles").EnumerateArray()).GetString());
                    Assert.False(sidecar.TryGetProperty("ports", out _));
                }
            }
            Assert.False(File.Exists(Path.Combine(root, ".env")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Compose_PreservesExplicitNamedDataVolume()
    {
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["ALLSTARR_DATA_PATH"] = "allstarr-data";
        foreach (var argument in new[] { "compose", "-f", "docker-compose.yml", "--env-file", ".env.example", "config", "--format", "json" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, await error);
        using var document = JsonDocument.Parse(await output);
        var mount = Assert.Single(document.RootElement.GetProperty("services").GetProperty("allstarr")
            .GetProperty("volumes").EnumerateArray());
        Assert.Equal("volume", mount.GetProperty("type").GetString());
        Assert.Equal("allstarr-data", mount.GetProperty("source").GetString());
        Assert.Equal("allstarr_allstarr-data", document.RootElement.GetProperty("volumes")
            .GetProperty("allstarr-data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task LegacyCompose_RendersWithoutChangingItsSourceFiles()
    {
        var fixture = Path.Combine(_repositoryRoot, "allstarr.Tests", "Fixtures", "LegacyV2");
        var composePath = Path.Combine(fixture, "docker-compose.yml");
        var environmentPath = Path.Combine(fixture, "example.env");
        var compose = File.ReadAllBytes(composePath);
        var environment = File.ReadAllBytes(environmentPath);
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = fixture,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "compose", "-f", composePath, "--env-file", environmentPath, "config", "--services" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        Assert.Equal(new[] { "allstarr", "spotify-lyrics", "valkey" }, (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal(compose, File.ReadAllBytes(composePath));
        Assert.Equal(environment, File.ReadAllBytes(environmentPath));
    }

    [Fact]
    public void RuntimeImage_UsesDataFolderAndPinnedDotnetBases()
    {
        var dockerfile = File.ReadAllText(Path.Combine(_repositoryRoot, "Dockerfile"));

        Assert.Contains("sdk:10.0.301@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("aspnet:10.0.9@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("node:22.23.1-alpine3.23@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY webui/package.json webui/package-lock.json", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY --from=webui /src/webui/build ./wwwroot/", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ca-certificates curl", dockerfile, StringComparison.Ordinal);
        Assert.Contains("/app/data/backups", dockerfile, StringComparison.Ordinal);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repositoryRoot, "allstarr", "appsettings.json")));
        Assert.Equal("/app/data/apple-upload", settings.RootElement.GetProperty("AppleDownload").GetProperty("SetupUploadDirectory").GetString());
    }

    [Fact]
    public void ContinuousIntegration_ExercisesSingleComposeModel()
    {
        var workflow = File.ReadAllText(Path.Combine(_repositoryRoot, ".github", "workflows", "ci.yml"));

        Assert.DoesNotContain("postgres", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DOTNET_VERSION: \"10.0.301\"", workflow, StringComparison.Ordinal);
        Assert.Contains("NODE_VERSION: \"22.23.1\"", workflow, StringComparison.Ordinal);
        Assert.Contains("working-directory: webui", workflow, StringComparison.Ordinal);
        Assert.Contains("npm run check", workflow, StringComparison.Ordinal);
        Assert.Contains("npm run build", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet format allstarr.sln --no-restore --verify-no-changes --verbosity minimal", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test --configuration Release --no-build", workflow, StringComparison.Ordinal);
        Assert.Contains("docker compose -f docker-compose.yml config --quiet", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose -f docker-compose.yml --profile spotify-lyrics config --quiet", workflow, StringComparison.Ordinal);
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
        Assert.Contains("Storage__DataDirectory=/app/data", workflow, StringComparison.Ordinal);
        Assert.Contains("Backend__Type=Jellyfin", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test -s /app/data/allstarr.db", workflow, StringComparison.Ordinal);
        Assert.Contains("test -s /app/data/keyring.json", workflow, StringComparison.Ordinal);
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
