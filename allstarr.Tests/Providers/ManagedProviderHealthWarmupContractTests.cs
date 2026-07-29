namespace allstarr.Tests;

public sealed class ManagedProviderHealthWarmupContractTests
{
    [Fact]
    public void ProductionHost_WarmsEnabledManagedAccountsWithoutBlockingStartup()
    {
        var program = File.ReadAllText(FindRepositoryFile("allstarr", "Program.cs"));
        var service = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Services", "Common", "ManagedProviderAccountHealthWarmupService.cs"));

        Assert.Contains("AddHostedService<ManagedProviderAccountHealthWarmupService>()", program, StringComparison.Ordinal);
        Assert.Contains("!builder.Environment.IsEnvironment(\"Testing\")", program, StringComparison.Ordinal);
        Assert.Contains(": BackgroundService", service, StringComparison.Ordinal);
        Assert.Contains("item.Enabled && item.SecretReferenceId != null", service, StringComparison.Ordinal);
        Assert.Contains("CanTestCapability", service, StringComparison.Ordinal);
        Assert.Contains("TestManagedProviderCapabilityAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("logger.LogWarning(ex,", service, StringComparison.Ordinal);
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
