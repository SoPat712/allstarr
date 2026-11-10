using allstarr.Core.Health;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed partial class ProviderStatusManagerTests
{
    [Theory]
    [InlineData("download-not-verified")]
    [InlineData("sample-unavailable")]
    [InlineData("playlist-sample-unavailable")]
    [InlineData("probe-limit-exceeded")]
    [InlineData("probe_not_available")]
    public void NeutralTryResultsRemainInHistoryWithoutChangingHealthOrAutomaticUse(string reason)
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var health = new ProviderRuntimeHealth(clock: clock);
        var manager = CreateManager(new Dictionary<string, string?>(), clock: clock, health: health);
        var key = ProviderRuntimeStatusKey.CreateAccountFree("deezer", "metadata");
        manager.RecordTryResult(key.Provider, key.Capability, null, null, false, 10, reason);
        Assert.Null(health.Get(key));
        var neutral = Assert.Single(manager.RecentAttempts(key, null)).Status;
        Assert.Equal(reason, neutral.ReasonCode);
        Assert.Equal(ProviderHealthState.Unknown, neutral.Health);
        Assert.False(neutral.HasCheckResult);
        Assert.Equal(ProviderHealthState.Unknown, manager.GetAccountFreeStatus(key.Provider, key.Capability).Health);

        manager.RecordTryResult(key.Provider, key.Capability, null, null, true, 7, null);
        var healthy = manager.GetAccountFreeStatus(key.Provider, key.Capability);
        manager.RestoreCheckResult(key, null, healthy);
        var observation = health.Get(key);
        for (var index = 0; index < 4; index++)
        {
            clock.UtcNow += TimeSpan.FromSeconds(1);
            manager.RecordTryResult(key.Provider, key.Capability, null, null, false, 10, reason);
        }
        Assert.Same(observation, health.Get(key));
        Assert.Equal(healthy, manager.GetAccountFreeStatus(key.Provider, key.Capability));
        Assert.False(manager.GetCurrentRuntimeEvidence(key, null, healthy).CircuitOpen);
        Assert.Equal(5, manager.RecentAttempts(key, null).Count(item => !item.Status.HasCheckResult));
        Assert.Single(health.AlertSamples(key, null));
    }
}
