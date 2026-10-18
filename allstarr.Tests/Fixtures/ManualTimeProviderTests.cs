namespace allstarr.Tests;

public sealed class ManualTimeProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForTimers_ObservesLiveTimersInAnyOrderBeforeOrAfterListening(bool listenFirst)
    {
        var clock = new ManualTimeProvider();
        var expected = new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1) };
        var waiting = listenFirst ? clock.WaitForTimersAsync(expected) : null;
        using var disposed = clock.CreateTimer(_ => { }, null, expected[0], Timeout.InfiniteTimeSpan);
        disposed.Dispose();
        using var gate = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(35), Timeout.InfiniteTimeSpan);
        using var changed = clock.CreateTimer(_ => { }, null, expected[0], Timeout.InfiniteTimeSpan);
        changed.Change(expected[1], Timeout.InfiniteTimeSpan);
        using var request = clock.CreateTimer(_ => { }, null, expected[0], Timeout.InfiniteTimeSpan);

        await (waiting ?? clock.WaitForTimersAsync(expected)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WaitForTimers_WaitsForEveryMatchingTimer()
    {
        var clock = new ManualTimeProvider();
        var delay = TimeSpan.FromSeconds(1);
        var waiting = clock.WaitForTimersAsync(delay, delay);
        using var first = clock.CreateTimer(_ => { }, null, delay, Timeout.InfiniteTimeSpan);
        Assert.False(waiting.IsCompleted);
        using var second = clock.CreateTimer(_ => { }, null, delay, Timeout.InfiniteTimeSpan);

        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
