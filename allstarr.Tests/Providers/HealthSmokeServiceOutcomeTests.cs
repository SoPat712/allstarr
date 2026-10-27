using allstarr.Core.Health;

namespace allstarr.Tests;

public sealed class HealthSmokeServiceOutcomeTests
{
    [Fact]
    public void InterruptedLegacyListenerRunWithOnlyCleanupPassedIsNotSuccessful()
    {
        var results = HealthSmokeProtocolProbe.StepNames
            .Select(name => new HealthSmokeStep(name, name == "cleanup" ? "passed" : "skipped", 0,
                name == "cleanup" ? null : "run_interrupted"))
            .ToArray();

        Assert.False(HealthSmokeService.IsSuccessful("listener", results));
    }
}
