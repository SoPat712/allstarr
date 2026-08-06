namespace allstarr.Tests;

public sealed class ProviderCtsWarmupContractTests
{
    [Fact]
    public void WarmupRotatesColdMeasurementsWithoutBlockingStartup()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "allstarr", "Services", "Common", "ProviderCtsWarmupService.cs"));
        var program = File.ReadAllText(Path.Combine(root, "allstarr", "Program.cs"));

        Assert.Contains(": BackgroundService", service, StringComparison.Ordinal);
        Assert.Contains("InitialDelay", service, StringComparison.Ordinal);
        Assert.Contains("PeriodicTimer", service, StringComparison.Ordinal);
        Assert.Contains("ProviderCapabilityKind.Streaming", service, StringComparison.Ordinal);
        Assert.Contains("ProviderAccountRequirement.None", service, StringComparison.Ordinal);
        Assert.Contains("runner.MeasureAsync(", service, StringComparison.Ordinal);
        Assert.Contains("ProviderAudioQuality.Any", service, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<ProviderCtsWarmupService>()", program, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerPersistsHealthyAndFailedColdMeasurements()
    {
        var root = FindRepositoryRoot();
        var runner = File.ReadAllText(Path.Combine(root, "allstarr", "Services", "Common", "ProviderCtsDiagnosticRunner.cs"));

        Assert.Contains("NoCache = true", runner, StringComparison.Ordinal);
        Assert.Contains("NoStore = true", runner, StringComparison.Ordinal);
        Assert.Contains("ProviderHealthState.Healthy", runner, StringComparison.Ordinal);
        Assert.Contains("ProviderHealthState.Degraded", runner, StringComparison.Ordinal);
        Assert.Contains("SampleLimitBytes", runner, StringComparison.Ordinal);
        Assert.Contains("trackSelector.SelectAsync", runner, StringComparison.Ordinal);
        Assert.Contains("db.AuditEvents.Add", runner, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
