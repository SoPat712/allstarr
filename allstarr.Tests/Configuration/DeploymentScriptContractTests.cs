using System.Diagnostics;
using System.Text.Json;

namespace allstarr.Tests;

public sealed class DeploymentScriptContractTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"allstarr-script-{Guid.NewGuid():N}");
    private readonly string repository = FindRepositoryRoot();

    public DeploymentScriptContractTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "tools", "apple-provider"));
        foreach (var file in new[] { "allstarr.sh", "docker-compose.yml", ".env.example", "tools/develop.sh", "tools/docker-compose.source.yml" })
            File.Copy(Path.Combine(repository, file), Path.Combine(root, file));
        WriteExecutable("docker", """
            #!/usr/bin/env bash
            printf '%s\n' "$*" >> "$FIXTURE_COMMANDS"
            if [[ "$*" == *'config --format json'* ]]; then cat "$FIXTURE_MODEL"; fi
            if [[ "$*" == *' config' ]]; then
              if [[ "${FIXTURE_QUOTED_PROJECT:-false}" == true ]]; then
                printf 'name: "%s"\n' "${COMPOSE_PROJECT_NAME:-allstarr}"
              else
                printf 'name: %s\n' "${COMPOSE_PROJECT_NAME:-${FIXTURE_PROJECT:-allstarr}}"
              fi
            fi
            if [[ "$*" == *'config --images allstarr' ]]; then echo fixture/allstarr:current; fi
            if [[ "$*" == 'volume ls '* ]]; then
              if [[ "${FIXTURE_VOLUME_LIST_FAIL:-false}" == true ]]; then exit 1; fi
              if [[ "${FIXTURE_VOLUME_EXISTS:-false}" == true ]]; then printf '%s_allstarr-data\n' "${COMPOSE_PROJECT_NAME:-${FIXTURE_PROJECT:-allstarr}}"; fi
            fi
            if [[ "$*" == 'volume inspect '* && "${FIXTURE_VOLUME_EXISTS:-false}" != true ]]; then exit 1; fi
            if [[ "$*" == 'run '* ]]; then
              if [[ "${FIXTURE_VOLUME_CHECK_FAIL:-false}" == true ]]; then exit 125; fi
              if [[ "${FIXTURE_VOLUME_POPULATED:-false}" == true ]]; then exit 1; fi
            fi
            if [[ -n "${FIXTURE_FAIL:-}" && "$*" == *"$FIXTURE_FAIL"* ]]; then exit 17; fi
            """);
        WriteExecutable("git", """
            #!/usr/bin/env bash
            printf 'git %s\n' "$*" >> "$FIXTURE_COMMANDS"
            if [[ "$*" == *diff* && "${FIXTURE_DIRTY:-}" == true ]]; then exit 1; fi
            if [[ "$*" == *symbolic-ref* ]]; then echo fixture; fi
            """);
        File.WriteAllText(Path.Combine(root, "tools", "apple-provider", "prepare.sh"), """
            #!/usr/bin/env bash
            printf 'prepare %s\n' "$*" >> "$FIXTURE_COMMANDS"
            [[ "${FIXTURE_PREPARE_FAIL:-}" != true ]]
            """);
        File.WriteAllText(Path.Combine(root, "model.json"), JsonSerializer.Serialize(new
        {
            services = new { allstarr = new { volumes = new[] { new { type = "bind", target = "/app/data", source = Path.Combine(root, "custom data") } } } }
        }));
    }

    [Theory]
    [InlineData("up", false)]
    [InlineData("update", false)]
    [InlineData("up", true)]
    [InlineData("update", true)]
    public async Task Release_StartsWithoutEnvironmentOrSourceBuild(string command, bool apple)
    {
        if (apple) File.WriteAllText(Path.Combine(root, ".allstarr-profiles"), "apple\n");
        var result = await RunAsync("allstarr.sh", [command]);
        Assert.Equal(0, result.Code);
        Assert.False(File.Exists(Path.Combine(root, ".env")));
        Assert.Equal(command == "update", Commands.Any(line => line.EndsWith("pull allstarr", StringComparison.Ordinal)));
        Assert.DoesNotContain(Commands, line => line.StartsWith("git ", StringComparison.Ordinal) || line.EndsWith("build allstarr", StringComparison.Ordinal));
        Assert.Equal(apple, Commands.Any(line => line.EndsWith("build apple-gateway", StringComparison.Ordinal)));
        var start = Array.FindIndex(Commands, line => line.Contains("up -d", StringComparison.Ordinal));
        Assert.True(start >= 0);
        Assert.Contains("--remove-orphans --wait --wait-timeout 180", Commands[start], StringComparison.Ordinal);
        if (apple) Assert.True(Array.FindIndex(Commands, line => line.EndsWith("build apple-gateway", StringComparison.Ordinal)) < start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionVerifier_EnableAndDisablePreservesSavedConnections(bool source)
    {
        if (source) File.WriteAllText(Path.Combine(root, ".allstarr-mode"), "source\n");
        Assert.Equal(0, (await RunAsync("allstarr.sh", ["enable", "sessions"])).Code);
        Assert.Equal("sessions\n", File.ReadAllText(Path.Combine(root, ".allstarr-profiles")));
        Assert.Contains(Commands, line => line.Contains("--profile sessions", StringComparison.Ordinal) &&
            line.EndsWith("build session-verifier", StringComparison.Ordinal));
        Assert.DoesNotContain(Commands, line => line.Contains("volume rm", StringComparison.Ordinal));

        Assert.Equal(0, (await RunAsync("allstarr.sh", ["disable", "sessions"])).Code);
        Assert.DoesNotContain("sessions", File.ReadAllText(Path.Combine(root, ".allstarr-profiles")), StringComparison.Ordinal);
        Assert.Contains(Commands, line => line.EndsWith("stop session-verifier", StringComparison.Ordinal));
        Assert.Contains(Commands, line => line.EndsWith("rm -f session-verifier", StringComparison.Ordinal));
        Assert.DoesNotContain(Commands, line => line.Contains("down -v", StringComparison.Ordinal) || line.Contains("--volumes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("up", false, false)]
    [InlineData("update", true, true)]
    public async Task Release_PreservesExistingVolumeBeforeRecreation(string command, bool existingEnvironment, bool emptyData)
    {
        var environmentPath = Path.Combine(root, ".env");
        const string original = "# Local settings\nUNRELATED=preserved";
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        if (existingEnvironment)
        {
            File.WriteAllText(environmentPath, original);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(environmentPath, mode);
        }
        if (emptyData) Directory.CreateDirectory(Path.Combine(root, "data"));

        var result = await RunAsync("allstarr.sh", [command], new() { ["FIXTURE_VOLUME_EXISTS"] = "true" });

        Assert.Equal(0, result.Code);
        Assert.Equal((existingEnvironment ? original : "") + "\nALLSTARR_DATA_PATH=allstarr-data\n", File.ReadAllText(environmentPath));
        if (!OperatingSystem.IsWindows()) Assert.Equal(existingEnvironment ? mode : UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(environmentPath));
        Assert.Contains("Retaining existing Allstarr data volume allstarr_allstarr-data.", result.Output, StringComparison.Ordinal);
        Assert.Contains("volume inspect allstarr_allstarr-data", Commands);
        Assert.DoesNotContain(Commands, line => line.StartsWith("run ", StringComparison.Ordinal));
        Assert.True(Array.FindIndex(Commands, line => line.StartsWith("volume inspect ", StringComparison.Ordinal)) < Array.FindIndex(Commands, line => line.Contains("up -d", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false, "custom-project")]
    [InlineData(true, "custom-project")]
    [InlineData(true, "123")]
    public async Task Release_ResolvesExistingVolumeFromComposeProject(bool environmentProject, string project)
    {
        if (!environmentProject) File.WriteAllText(Path.Combine(root, ".env"), "COMPOSE_PROJECT_NAME=" + project + "\n");
        var options = new Dictionary<string, string> { ["FIXTURE_VOLUME_EXISTS"] = "true" };
        options[environmentProject ? "COMPOSE_PROJECT_NAME" : "FIXTURE_PROJECT"] = project;
        options["FIXTURE_QUOTED_PROJECT"] = (project == "123").ToString().ToLowerInvariant();

        Assert.Equal(0, (await RunAsync("allstarr.sh", ["up"], options)).Code);

        Assert.Contains($"volume inspect {project}_allstarr-data", Commands);
        Assert.Contains("ALLSTARR_DATA_PATH=allstarr-data\n", File.ReadAllText(Path.Combine(root, ".env")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_PopulatedLocalDataWithoutPopulatedVolumeStaysUnchanged(bool volumeExists)
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "data", ".saved"), "preserved");

        Assert.Equal(0, (await RunAsync("allstarr.sh", ["up"], new() { ["FIXTURE_VOLUME_EXISTS"] = volumeExists.ToString().ToLowerInvariant() })).Code);

        Assert.False(File.Exists(Path.Combine(root, ".env")));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(root, "data", ".saved")));
        Assert.Equal(volumeExists, Commands.Any(line => line.StartsWith("run ", StringComparison.Ordinal)));
        if (volumeExists)
        {
            var check = Assert.Single(Commands, line => line.StartsWith("run ", StringComparison.Ordinal));
            Assert.Contains("--pull never --network none --read-only", check, StringComparison.Ordinal);
            Assert.Contains("type=volume,src=allstarr_allstarr-data,dst=/existing,readonly,volume-nocopy", check, StringComparison.Ordinal);
            Assert.Contains("--entrypoint /bin/sh fixture/allstarr:current", check, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_ConflictingDataOrFailedVolumeCheckPreventsRecreation(bool checkFails)
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "data", "saved"), "preserved");
        var result = await RunAsync("allstarr.sh", ["update"], new()
        {
            ["FIXTURE_VOLUME_EXISTS"] = "true",
            ["FIXTURE_VOLUME_POPULATED"] = "true",
            ["FIXTURE_VOLUME_CHECK_FAIL"] = checkFails.ToString().ToLowerInvariant()
        });

        Assert.NotEqual(0, result.Code);
        Assert.Contains("ALLSTARR_DATA_PATH", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, ".env")));
        Assert.DoesNotContain(Commands, line => line.Contains("up -d", StringComparison.Ordinal));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(root, "data", "saved")));
    }

    [Theory]
    [InlineData("FIXTURE_VOLUME_LIST_FAIL", "true")]
    [InlineData("FIXTURE_FAIL", "volume inspect")]
    public async Task Release_FailedVolumeInspectionPreventsRecreation(string key, string value)
    {
        var result = await RunAsync("allstarr.sh", ["up"], new() { ["FIXTURE_VOLUME_EXISTS"] = "true", [key] = value });

        Assert.NotEqual(0, result.Code);
        Assert.Contains("ALLSTARR_DATA_PATH", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, ".env")));
        Assert.DoesNotContain(Commands, line => line.Contains("up -d", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "./chosen")]
    [InlineData(true, "allstarr-data")]
    [InlineData(true, "")]
    public async Task Release_ExplicitDataPathSkipsVolumeSelection(bool environmentSetting, string value)
    {
        var contents = environmentSetting ? "UNRELATED=preserved\n" : $"ALLSTARR_DATA_PATH={value}\n";
        File.WriteAllText(Path.Combine(root, ".env"), contents);
        var options = new Dictionary<string, string> { ["FIXTURE_VOLUME_EXISTS"] = "true", ["FIXTURE_VOLUME_POPULATED"] = "true" };
        if (environmentSetting) options["ALLSTARR_DATA_PATH"] = value;

        Assert.Equal(0, (await RunAsync("allstarr.sh", ["up"], options)).Code);

        Assert.Equal(contents, File.ReadAllText(Path.Combine(root, ".env")));
        Assert.DoesNotContain(Commands, line => line.StartsWith("volume inspect ", StringComparison.Ordinal) || line.StartsWith("run ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("up", "config --quiet")]
    [InlineData("update", "pull allstarr")]
    [InlineData("up", "build apple-gateway")]
    [InlineData("update", "build apple-gateway")]
    public async Task Release_FailuresPreventRecreation(string command, string failure)
    {
        File.WriteAllText(Path.Combine(root, ".allstarr-profiles"), "apple\n");
        var result = await RunAsync("allstarr.sh", [command], new() { ["FIXTURE_FAIL"] = failure });
        Assert.NotEqual(0, result.Code);
        Assert.DoesNotContain(Commands, line => line.Contains("up -d", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not a setting\n")]
    [InlineData("KEY=first\nKEY=second\n")]
    public async Task Release_InvalidEnvironmentPreventsDocker(string contents)
    {
        File.WriteAllText(Path.Combine(root, ".env"), contents);
        Assert.NotEqual(0, (await RunAsync("allstarr.sh", ["update"])).Code);
        Assert.Empty(Commands);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("update")]
    public async Task Release_SavedSourceModeRequiresDeveloperScript(string command)
    {
        File.WriteAllText(Path.Combine(root, ".allstarr-mode"), "source\n");
        var result = await RunAsync("allstarr.sh", [command]);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("tools/develop.sh", result.Error, StringComparison.Ordinal);
        Assert.Empty(Commands);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("restore")]
    [InlineData("mode")]
    [InlineData("init")]
    [InlineData("upgrade")]
    public async Task RemovedCommands_DoNotInvokeDocker(string command)
    {
        Assert.Equal(2, (await RunAsync("allstarr.sh", [command])).Code);
        Assert.Empty(Commands);
        Assert.False(File.Exists(Path.Combine(root, ".env")));
        Assert.False(File.Exists(Path.Combine(root, ".allstarr-mode")));
    }

    [Theory]
    [InlineData("enable", "arm64-v8a", false)]
    [InlineData("enable", "x86_64", true)]
    [InlineData("install-apple", "x86_64", false)]
    [InlineData("prepare-apple", "x86_64", false)]
    public async Task Apple_VerifiesStagedUploadBeforeBuildAndEnable(string command, string arch, bool source)
    {
        var upload = Path.Combine(root, "custom data", "apple-upload");
        Directory.CreateDirectory(upload);
        var package = Path.Combine(upload, "fixture package.apkm");
        File.WriteAllText(package, "synthetic-package");
        File.WriteAllText(Path.Combine(root, ".env"), "UNRELATED=preserved\nAPPLE_WRAPPER_TARGET_ARCH=obsolete\n");
        if (source) File.WriteAllText(Path.Combine(root, ".allstarr-mode"), "source\n");
        var arguments = command == "enable" ? new[] { command, "apple", arch } : new[] { command, arch };
        Assert.Equal(0, (await RunAsync("allstarr.sh", arguments)).Code);
        var commands = Commands;
        var prepare = Array.FindIndex(commands, line => line.StartsWith("prepare ", StringComparison.Ordinal));
        var build = Array.FindIndex(commands, line => line.EndsWith("build apple-wrapper apple-gateway", StringComparison.Ordinal));
        Assert.Contains($"--apkm {package} --arch {arch}", commands[prepare], StringComparison.Ordinal);
        Assert.True(prepare >= 0 && build > prepare);
        Assert.Contains("apple", File.ReadAllLines(Path.Combine(root, ".allstarr-profiles")));
        var environment = File.ReadAllText(Path.Combine(root, ".env"));
        Assert.Contains("UNRELATED=preserved", environment, StringComparison.Ordinal);
        Assert.Contains($"APPLE_WRAPPER_TARGET_ARCH={arch}", environment, StringComparison.Ordinal);
        Assert.Contains($"APPLE_WRAPPER_RUNTIME_PLATFORM=linux/{(arch == "x86_64" ? "amd64" : "arm64")}", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("obsolete", environment, StringComparison.Ordinal);
        Assert.Equal(command != "prepare-apple", commands.Any(line => line.Contains("up -d", StringComparison.Ordinal)));
        Assert.Equal(source, commands.Any(line => line.EndsWith("build allstarr", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apple_FailedPreparationOrBuildDoesNotEnableOrStart(bool preparation)
    {
        var package = Path.Combine(root, "fixture.apk");
        File.WriteAllText(package, "synthetic-package");
        var options = preparation ? new Dictionary<string, string> { ["FIXTURE_PREPARE_FAIL"] = "true" }
            : new() { ["FIXTURE_FAIL"] = "build apple-wrapper apple-gateway" };
        Assert.NotEqual(0, (await RunAsync("allstarr.sh", ["enable", "apple", package, "x86_64"], options)).Code);
        Assert.False(File.Exists(Path.Combine(root, ".allstarr-profiles")));
        Assert.DoesNotContain(Commands, line => line.Contains("up -d", StringComparison.Ordinal));
        if (preparation) Assert.DoesNotContain(Commands, line => line.Contains("build ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apple_MissingPackageDoesNotEnableOrBuild()
    {
        Assert.NotEqual(0, (await RunAsync("allstarr.sh", ["enable", "apple", "x86_64"])).Code);
        Assert.False(File.Exists(Path.Combine(root, ".allstarr-profiles")));
        Assert.DoesNotContain(Commands, line => line.Contains("build ", StringComparison.Ordinal) || line.Contains("up -d", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apple_DisableRemovesOnlyOptionalContainersAndPreservesFiles()
    {
        File.WriteAllText(Path.Combine(root, ".allstarr-profiles"), "apple\n");
        File.WriteAllText(Path.Combine(root, "session"), "preserved");
        Assert.Equal(0, (await RunAsync("allstarr.sh", ["disable", "apple"])).Code);
        Assert.DoesNotContain("apple", File.ReadAllLines(Path.Combine(root, ".allstarr-profiles")));
        Assert.Contains(Commands, line => line.EndsWith("stop apple-gateway apple-wrapper", StringComparison.Ordinal));
        Assert.Contains(Commands, line => line.EndsWith("rm -f apple-gateway apple-wrapper", StringComparison.Ordinal));
        Assert.DoesNotContain(Commands, line => line.Contains("down", StringComparison.Ordinal) || line.Contains("prune", StringComparison.Ordinal) || line.Contains("-v", StringComparison.Ordinal));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(root, "session")));
    }

    [Theory]
    [InlineData("status", "ps")]
    [InlineData("logs", "logs --tail=200 -f allstarr")]
    public async Task Inspection_UsesSavedProfilesWithoutChangingState(string command, string expected)
    {
        File.WriteAllText(Path.Combine(root, ".allstarr-profiles"), "apple\n");
        var arguments = command == "logs" ? new[] { command, "allstarr" } : new[] { command };
        Assert.Equal(0, (await RunAsync("allstarr.sh", arguments)).Code);
        Assert.Single(Commands);
        Assert.Contains("--profile apple", Commands[0], StringComparison.Ordinal);
        Assert.EndsWith(expected, Commands[0], StringComparison.Ordinal);
        Assert.Equal("apple\n", File.ReadAllText(Path.Combine(root, ".allstarr-profiles")));
    }

    [Theory]
    [InlineData("up", false, false)]
    [InlineData("up", true, false)]
    [InlineData("update", false, false)]
    [InlineData("update", true, false)]
    [InlineData("update", false, true)]
    public async Task Developer_SourceBuildsAreIsolatedAndRejectDirtyUpdates(string command, bool apple, bool dirty)
    {
        File.WriteAllText(Path.Combine(root, ".allstarr-profiles"), apple ? "apple\n" : "");
        Assert.Equal(0, (await RunAsync("tools/develop.sh", ["mode", "source"])).Code);
        var result = await RunAsync("tools/develop.sh", [command], new() { ["FIXTURE_DIRTY"] = dirty.ToString().ToLowerInvariant() });
        Assert.Equal(!dirty, result.Code == 0);
        if (dirty)
        {
            Assert.DoesNotContain(Commands, line => line.Contains("pull --ff-only", StringComparison.Ordinal) || line.Contains("build allstarr", StringComparison.Ordinal));
            return;
        }
        Assert.Contains(Commands, line => line.Contains("tools/docker-compose.source.yml", StringComparison.Ordinal) && line.EndsWith("build allstarr", StringComparison.Ordinal));
        Assert.Equal(command == "update", Commands.Any(line => line.EndsWith("pull --ff-only", StringComparison.Ordinal)));
        Assert.DoesNotContain(Commands, line => line.Contains("prune", StringComparison.Ordinal) || line.EndsWith("pull allstarr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Developer_DefaultSourceModeSurvivesAppleEnable()
    {
        Assert.Equal(0, (await RunAsync("tools/develop.sh", ["up"])).Code);
        var package = Path.Combine(root, "fixture.apk");
        File.WriteAllText(package, "synthetic-package");

        Assert.Equal(0, (await RunAsync("allstarr.sh", ["enable", "apple", package, "x86_64"])).Code);

        var starts = Commands.Where(line => line.Contains("up -d", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.All(starts, line => Assert.Contains("tools/docker-compose.source.yml", line, StringComparison.Ordinal));
        Assert.Equal("source\n", File.ReadAllText(Path.Combine(root, ".allstarr-mode")));
    }

    private string[] Commands => File.Exists(Path.Combine(root, "commands")) ? File.ReadAllLines(Path.Combine(root, "commands")) : [];

    private async Task<(int Code, string Error, string Output)> RunAsync(string script, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("bash") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Path.Combine(root, script));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["FIXTURE_COMMANDS"] = Path.Combine(root, "commands");
        start.Environment["FIXTURE_MODEL"] = Path.Combine(root, "model.json");
        start.Environment.Remove("ALLSTARR_DATA_PATH");
        start.Environment.Remove("COMPOSE_PROJECT_NAME");
        foreach (var pair in environment ?? []) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await error, await output);
    }

    private void WriteExecutable(string name, string contents)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, contents + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate allstarr.sln");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
