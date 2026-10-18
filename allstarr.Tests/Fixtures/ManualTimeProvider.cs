using System.Threading.Channels;

namespace allstarr.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset? initialUtcNow = null) : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private readonly Channel<(ManualTimer Timer, int Version, TimeSpan Delay)> scheduled = Channel.CreateUnbounded<(ManualTimer, int, TimeSpan)>();
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);
    public override DateTimeOffset GetUtcNow() => (initialUtcNow ?? DateTimeOffset.UnixEpoch).AddTicks(GetTimestamp());
    public async Task<TimeSpan> NextTimerAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await NextTimerAsync(timeout.Token);
    }

    // Callers must keep the expected timers live until they have been observed.
    public async Task WaitForTimersAsync(params TimeSpan[] delays)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var remaining = delays.ToList();
        while (remaining.Count > 0)
            remaining.Remove(await NextTimerAsync(timeout.Token));
    }

    private async Task<TimeSpan> NextTimerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var next = await scheduled.Reader.ReadAsync(cancellationToken);
            lock (timers)
                if (next.Timer.IsScheduled(next.Version)) return next.Delay;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        List<ManualTimer> due;
        lock (timers)
        {
            Interlocked.Add(ref timestamp, amount.Ticks);
            due = timers.Where(timer => timer.Due <= timestamp).ToList();
            foreach (var timer in due) timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; private set; }
        private bool disposed;
        private bool fired;
        private int version;
        public bool IsScheduled(int expectedVersion) => !disposed && !fired && version == expectedVersion;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (clock.timers)
            {
                if (disposed) return false;
                clock.timers.Remove(this);
                version++;
                fired = dueTime == Timeout.InfiniteTimeSpan;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = clock.timestamp + dueTime.Ticks;
                    clock.timers.Add(this);
                    clock.scheduled.Writer.TryWrite((this, version, dueTime));
                }
                return true;
            }
        }
        public void Fire()
        {
            lock (clock.timers)
            {
                if (disposed || fired) return;
                fired = true;
                callback(state);
            }
        }
        public void Dispose()
        {
            lock (clock.timers)
            {
                disposed = true;
                clock.timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
