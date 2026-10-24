using allstarr.Core.Health;

namespace allstarr.Tests;

public sealed class HealthSmokeAlertPolicyTests
{
    [Fact]
    public void AFirstFailureDoesNotAlert()
    {
        var result = HealthSmokeAlertPolicy.Evaluate([], [new("playback", "failed", 10, "timeout")], []);
        Assert.Empty(result.Changes);
        Assert.Empty(result.OpenSteps);
    }

    [Fact]
    public void TwoConsecutiveFailuresOpenOneAlertUntilRecovery()
    {
        HealthSmokeStep[] failure = [new("playback", "failed", 10, "timeout")];
        var second = HealthSmokeAlertPolicy.Evaluate(failure, failure, []);
        Assert.Equal([new HealthSmokeAlertChange("playback", "failed")], second.Changes);
        Assert.Equal(["playback"], second.OpenSteps);
        var third = HealthSmokeAlertPolicy.Evaluate(failure, failure, second.OpenSteps);
        Assert.Empty(third.Changes);
        var recovery = HealthSmokeAlertPolicy.Evaluate(failure, [new("playback", "passed", 5)], third.OpenSteps);
        Assert.Equal([new HealthSmokeAlertChange("playback", "recovered")], recovery.Changes);
        Assert.Empty(recovery.OpenSteps);
    }

    [Fact]
    public void FailuresInDifferentStepsDoNotCountAsConsecutive()
    {
        var result = HealthSmokeAlertPolicy.Evaluate([new("lyrics", "failed", 10, "timeout")],
            [new("playback", "failed", 10, "timeout"), new("lyrics", "passed", 1)], []);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void ASkippedStepCannotSendARecovery()
    {
        var result = HealthSmokeAlertPolicy.Evaluate([new("playback", "failed", 10, "timeout")],
            [new("playback", "skipped", 0, "search_required")], ["playback"]);
        Assert.Empty(result.Changes);
        Assert.Equal(["playback"], result.OpenSteps);
    }
}
