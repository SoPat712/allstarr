namespace allstarr.Tests;

public sealed class AppleSessionStorageContractTests
{
    [Theory]
    [InlineData("apple-gateway-data", "/data")]
    [InlineData("apple-wrapper-session", "/app/rootfs/data/data/com.apple.android.music/files")]
    public void AppleVolumes_RemainSeparateFromTheApplicationDataFolder(string volumeName, string mountPath)
    {
        var compose = File.ReadAllText(FindRepositoryFile("docker-compose.yml"));
        Assert.Contains($"{volumeName}:{mountPath}", compose, StringComparison.Ordinal);
        Assert.Contains("allstarr-data}:/app/data", compose, StringComparison.Ordinal);
        var documentation = File.ReadAllText(FindRepositoryFile("docs", "operations", "storage.md"));
        Assert.Contains("Apple gateway and wrapper sessions remain in their own profile volumes", documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupDocumentation_RequiresSeparateOptionalProviderSessionCopies()
    {
        var documentation = File.ReadAllText(FindRepositoryFile("docs", "operations", "deployment-profiles.md"));
        Assert.Contains("excludes media, extension packages, cache, deployment configuration, and optional provider sessions", documentation, StringComparison.Ordinal);
        Assert.Contains("copy the complete data folder", documentation, StringComparison.Ordinal);
        Assert.Contains("optional provider volumes", documentation, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."),
            Path.Combine(parts));
    }
}
