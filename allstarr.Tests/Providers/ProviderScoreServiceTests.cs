using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ProviderScoreServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static (string, string, DateTimeOffset, bool, long?, string?) Row(string provider, int daysAgo, bool passed, long? latency = 500, string capability = "metadata", string? reason = null) =>
        (provider, capability, Now.AddDays(-daysAgo), passed, latency, reason);

    [Fact]
    public void AReliableFastProviderScoresAtTheTop()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 10).Select(day => Row("apple", day, true)), Now));
        Assert.Equal(100, score.Score);
        Assert.Equal(100d, score.UptimePercent);
    }

    [Fact]
    public void RecentFailuresCostMoreThanOldOnes()
    {
        var recent = Assert.Single(ProviderScoreService.ForTest(
            Enumerable.Range(0, 5).Select(day => Row("a", day, false)).Concat(Enumerable.Range(20, 5).Select(day => Row("a", day, true))), Now));
        var old = Assert.Single(ProviderScoreService.ForTest(
            Enumerable.Range(0, 5).Select(day => Row("a", day, true)).Concat(Enumerable.Range(20, 5).Select(day => Row("a", day, false))), Now));
        Assert.True(old.Score > recent.Score);
        Assert.Equal(old.UptimePercent, recent.UptimePercent);
    }

    [Fact]
    public void SlowProvidersScoreBelowFastOnesWithTheSameUptime()
    {
        var fast = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 6).Select(day => Row("a", day, true, 400)), Now));
        var slow = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 6).Select(day => Row("a", day, true, 9000)), Now));
        Assert.Equal(100, fast.Score);
        Assert.Equal(80, slow.Score);
    }

    [Fact]
    public void TooFewChecksGiveNoScoreInsteadOfAGuess()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, ProviderScoreService.MinimumChecks - 1).Select(day => Row("a", day, true)), Now));
        Assert.Null(score.Score);
        Assert.Equal(4, score.Checks);
    }

    [Fact]
    public void CapabilitiesAreScoredSeparatelyAndKeepTheLastFailure()
    {
        var rows = Enumerable.Range(0, 6).Select(day => Row("a", day, true, capability: "metadata"))
            .Concat(Enumerable.Range(0, 6).Select(day => Row("a", day, false, capability: "download", reason: day == 0 ? "timeout" : "unreachable")));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now));
        Assert.Equal(100, score.Capabilities.Single(item => item.Capability == "metadata").Score);
        var download = score.Capabilities.Single(item => item.Capability == "download");
        Assert.Equal(6, download.Failures);
        Assert.Equal("timeout", download.LastFailureCode);
    }
}
