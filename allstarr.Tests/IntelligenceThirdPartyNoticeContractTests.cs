namespace allstarr.Tests;

public sealed class IntelligenceThirdPartyNoticeContractTests
{
    [Fact]
    public void ExternalIntelligenceStaysOutsideTheDistributionAndBundledNoticesExist()
    {
        var root = FindRepositoryRoot();
        var sdk = File.ReadAllText(Path.Combine(root, "docs", "extensions", "sdk-v1.md"));

        Assert.Contains("service implementation remain outside the Allstarr package", sdk,
            StringComparison.Ordinal);
        Assert.Contains("not a bundled registry or third-party extension packages", sdk,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "allstarr", "wwwroot", "licenses", "fonts", "Inter-OFL.txt")));
        Assert.True(File.Exists(Path.Combine(root, "allstarr", "wwwroot", "licenses", "fonts", "Sora-OFL.txt")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
            directory = directory.Parent;
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
