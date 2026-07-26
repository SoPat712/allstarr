namespace allstarr.Tests;

public sealed class AppleSessionPortableBackupContractTests
{
    private readonly string _script = File.ReadAllText(FindRepositoryFile("allstarr.sh"));

    [Theory]
    [InlineData("allstarr_apple-gateway-data", "volume-apple-gateway")]
    [InlineData("allstarr_apple-wrapper-session", "volume-apple-wrapper-session")]
    public void PortableBackup_PreservesOptionalAppleState(string volumeName, string archivePath)
    {
        Assert.Contains($"{volumeName}|{archivePath}", _script, StringComparison.Ordinal);
        Assert.Contains($"{archivePath}|{volumeName}", _script, StringComparison.Ordinal);
        Assert.Contains($"{archivePath}/*", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleVolumes_AreConditionalForNonAppleDeployments()
    {
        Assert.Contains("docker volume inspect \"$volume_name\"", _script, StringComparison.Ordinal);
        Assert.Contains("tar -tzf \"$staging/volume-data.tar.gz\" | grep -q", _script, StringComparison.Ordinal);
        Assert.Contains("Apple provider/session volumes when present", _script, StringComparison.Ordinal);
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
