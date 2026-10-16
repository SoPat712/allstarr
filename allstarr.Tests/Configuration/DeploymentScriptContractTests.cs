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

    private string[] Commands => File.Exists(Path.Combine(root, "commands")) ? File.ReadAllLines(Path.Combine(root, "commands")) : [];

    private async Task<(int Code, string Error)> RunAsync(string script, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("bash") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Path.Combine(root, script));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["FIXTURE_COMMANDS"] = Path.Combine(root, "commands");
        start.Environment["FIXTURE_MODEL"] = Path.Combine(root, "model.json");
        foreach (var pair in environment ?? []) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        return (process.ExitCode, await error);
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
