namespace allstarr.Tests;

public sealed class ManagedProviderHealthWarmupContractTests
{
    [Fact]
    public void HostRebuildsStatusWithoutManagedAccountWarmup()
    {
        var program = File.ReadAllText(FindRepositoryFile("allstarr", "Program.cs"));
        Assert.DoesNotContain("AddHostedService<ManagedProviderAccountHealthWarmupService>()", program, StringComparison.Ordinal);
        Assert.Contains("AddProviderRuntimeHealth", program, StringComparison.Ordinal);
        var monitor = File.ReadAllText(FindRepositoryFile("allstarr", "Core", "Operations", "SidecarHealthMonitor.cs"));
        Assert.DoesNotContain("TestManagedProvider", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain("GetStreamLease", monitor, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
