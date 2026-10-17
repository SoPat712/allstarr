using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed class ExtensionSessionStartupRetryTests
{
    [Fact]
    public async Task RetriesWithBoundedExponentialDelaysUntilNetworkIsReady()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var retry = ExtensionSessionStartupRetry.RunAsync(
            _ => Task.FromResult(Interlocked.Increment(ref calls) < 4),
            clock,
            CancellationToken.None);

        foreach (var expected in new[] { 10, 20, 40 })
        {
            Assert.Equal(TimeSpan.FromSeconds(expected), await clock.NextTimerAsync());
            clock.Advance(TimeSpan.FromSeconds(expected));
        }

        Assert.False(await retry);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task StopsAfterSixRetriesWhenNetworkNeverBecomesReady()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var retry = ExtensionSessionStartupRetry.RunAsync(
            _ => Task.FromResult(Interlocked.Increment(ref calls) > 0),
            clock,
            CancellationToken.None);

        foreach (var expected in new[] { 10, 20, 40, 60, 60, 60 })
        {
            Assert.Equal(TimeSpan.FromSeconds(expected), await clock.NextTimerAsync());
            clock.Advance(TimeSpan.FromSeconds(expected));
        }

        Assert.True(await retry);
        Assert.Equal(7, calls);
    }
}
