using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ProgressiveCachingStreamTests
{
    [Fact]
    public async Task InitialPreparationWindowIsSeparateFromTheSeekWaitBudget()
    {
        var clock = new ManualTimeProvider();
        await using var file = FixtureFile([]);
        await using var stream = new ProgressiveCachingStream(file,
            () => new(0, null, false, false, NeverCompletingTask(), NeverCompletingTask()), static () => { },
            TimeSpan.FromSeconds(8), initialWaitTimeout: TimeSpan.FromSeconds(60), timeProvider: clock,
            maximumWaitTimeout: TimeSpan.FromSeconds(8));
        var pending = stream.ReadAsync(new byte[1]).AsTask();
        Assert.Equal(TimeSpan.FromSeconds(60), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(52));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task UnknownLength_ReadsOnlyAdvertisedBytesAndAwaitsCompletionAtTrueEof()
    {
        var payload = Enumerable.Range(0, 8).Select(value => (byte)value).ToArray();
        await using var file = FixtureFile(payload);
        var available = 0L;
        var finished = false;
        var changed = Signal();
        var completion = Signal();
        var closed = 0;
        await using var reader = new ProgressiveCachingStream(
            file,
            () => new(available, null, finished, false, changed.Task, completion.Task),
            () => closed++,
            TimeSpan.FromSeconds(1));

        Assert.False(reader.CanSeek);
        Assert.Throws<NotSupportedException>(() => reader.Length);

        var firstBuffer = new byte[4];
        var firstRead = reader.ReadAsync(firstBuffer.AsMemory()).AsTask();
        Assert.False(firstRead.IsCompleted);
        available = 4;
        var firstChange = changed;
        changed = Signal();
        firstChange.SetResult();
        Assert.Equal(4, await firstRead);
        Assert.Equal(payload[..4], firstBuffer);

        var secondBuffer = new byte[4];
        var secondRead = reader.ReadAsync(secondBuffer.AsMemory()).AsTask();
        Assert.False(secondRead.IsCompleted);
        available = payload.Length;
        var secondChange = changed;
        changed = Signal();
        secondChange.SetResult();
        Assert.Equal(4, await secondRead);
        Assert.Equal(payload[4..], secondBuffer);

        var eofRead = reader.ReadAsync(new byte[1].AsMemory()).AsTask();
        Assert.False(eofRead.IsCompleted);
        finished = true;
        var eofChange = changed;
        changed = Signal();
        eofChange.SetResult();
        Assert.False(eofRead.IsCompleted);
        completion.SetResult();
        Assert.Equal(0, await eofRead);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task KnownLength_UsesByteAndMemoryReadsAndPreservesSeekPositions()
    {
        var payload = Enumerable.Range(0, 9).Select(value => (byte)(value + 10)).ToArray();
        await using var file = FixtureFile(payload);
        await using var reader = new ProgressiveCachingStream(
            file,
            () => new(payload.Length, payload.Length, true, false, Task.CompletedTask, Task.CompletedTask),
            static () => { },
            TimeSpan.FromSeconds(1));

        Assert.True(reader.CanSeek);
        Assert.Equal(payload.Length, reader.Length);
        Assert.Equal(0, reader.Position);

        var first = new byte[5];
        Assert.Equal(3, reader.Read(first, 1, 3));
        Assert.Equal(payload[..3], first[1..4]);
        Assert.Equal(3, reader.Position);
        Assert.Equal(2, reader.Seek(2, SeekOrigin.Begin));
        Assert.Equal(3, reader.Seek(1, SeekOrigin.Current));
        Assert.Equal(payload.Length - 2, reader.Seek(-2, SeekOrigin.End));
        var final = new byte[1];
        Assert.Equal(1, await reader.ReadAsync(final.AsMemory()));
        Assert.Equal(payload[^2], final[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Seek(long.MaxValue, SeekOrigin.Current));
    }

    [Fact]
    public async Task ReadBeyondAvailable_HonorsCallerCancellation()
    {
        await using var file = FixtureFile([1, 2, 3]);
        var changed = Signal();
        await using var reader = new ProgressiveCachingStream(
            file,
            () => new(0, null, false, false, changed.Task, Task.CompletedTask),
            static () => { },
            TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();

        var pending = reader.ReadAsync(new byte[1].AsMemory(), cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ReadBeyondAvailable_TimesOutWithIOException()
    {
        await using var file = FixtureFile([1, 2, 3]);
        var clock = new ManualTimeProvider();
        var entered = Signal();
        await using var reader = new ProgressiveCachingStream(
            file,
            () =>
            {
                entered.TrySetResult();
                return new(0, null, false, false, NeverCompletingTask(), NeverCompletingTask());
            },
            static () => { },
            TimeSpan.FromMilliseconds(10),
            timeProvider: clock);

        var pending = reader.ReadAsync(new byte[1].AsMemory()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromMilliseconds(10), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task FailedTransfer_ThrowsSafeIOExceptionWithoutProviderText()
    {
        await using var file = FixtureFile([1, 2, 3]);
        var completion = Signal();
        completion.SetException(new IOException("provider-secret https://signed.invalid/media"));
        await using var reader = new ProgressiveCachingStream(
            file,
            () => new(0, null, false, true, Task.CompletedTask, completion.Task),
            static () => { },
            TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1].AsMemory()).AsTask());
        Assert.DoesNotContain("provider-secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signed.invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZeroLengthRead_DoesNotSignalEof()
    {
        await using var file = FixtureFile([1, 2, 3]);
        var completion = Signal();
        var closed = 0;
        await using var reader = new ProgressiveCachingStream(
            file,
            () => new(0, null, false, false, Task.CompletedTask, completion.Task),
            () => closed++,
            TimeSpan.FromSeconds(1));

        Assert.Equal(0, await reader.ReadAsync(Memory<byte>.Empty));
        Assert.False(completion.Task.IsCompleted);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task StartedCallback_IsDeferredUntilFirstNonEmptyReadAndInvokedOnce()
    {
        await using var file = FixtureFile([1, 2]);
        var started = 0;
        await using var reader = new ProgressiveCachingStream(
            file,
            () => new(2, 2, true, false, Task.CompletedTask, Task.CompletedTask),
            static () => { },
            TimeSpan.FromSeconds(1),
            () => started++);

        Assert.True(reader.CanSeek);
        Assert.Equal(2, reader.Length);
        Assert.Equal(0, reader.Seek(0, SeekOrigin.Begin));
        Assert.Equal(0, started);
        Assert.Equal(0, await reader.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(0, started);

        var buffer = new byte[2];
        Assert.Equal(2, await reader.ReadAsync(buffer.AsMemory()));
        Assert.Equal(1, started);
        Assert.Equal(0, await reader.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task Dispose_ClosesFileAndInvokesClosedOnce()
    {
        var file = FixtureFile([1, 2, 3]);
        var closed = 0;
        var reader = new ProgressiveCachingStream(
            file,
            () => new(0, null, false, false, Task.CompletedTask, Task.CompletedTask),
            () => closed++,
            TimeSpan.FromSeconds(1));

        reader.Dispose();
        reader.Dispose();
        await reader.DisposeAsync();

        Assert.Equal(1, closed);
        Assert.False(file.CanRead);
    }

    [Fact]
    public async Task SeekAheadWait_StopsAtOverallDeadlineEvenWhileEarlierBytesArrive()
    {
        await using var file = FixtureFile(new byte[100]);
        var clock = new ManualTimeProvider();
        var available = 0;
        await using var reader = new ProgressiveCachingStream(file,
            () =>
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                return new(++available, 100, false, false, Task.CompletedTask, Task.CompletedTask);
            }, static () => { }, TimeSpan.FromSeconds(3), timeProvider: clock,
            maximumWaitTimeout: TimeSpan.FromSeconds(5));
        reader.Position = 90;
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1]).AsTask());
        Assert.InRange(available, 5, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLengthSeekWait_StopsOnCancellationOrDeadline(bool cancel)
    {
        await using var file = FixtureFile([1, 2, 3]);
        var clock = new ManualTimeProvider();
        await using var reader = new ProgressiveCachingStream(file,
            () => new(3, null, false, false, NeverCompletingTask(), NeverCompletingTask()),
            static () => { }, TimeSpan.FromSeconds(60), timeProvider: clock);
        using var cancellation = new CancellationTokenSource();
        var waiting = reader.WaitForRangeAsync(new System.Net.Http.Headers.RangeHeaderValue(8, null), cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(8), await clock.NextTimerAsync());
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(8));
            await Assert.ThrowsAsync<TimeoutException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    private static FileStream FixtureFile(ReadOnlySpan<byte> payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"allstarr-progressive-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, payload.ToArray());
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }

    [Fact]
    public async Task SeekAheadWait_SpuriousNotificationsDoNotResetTheStallTimeout()
    {
        await using var file = FixtureFile(new byte[100]);
        var clock = new ManualTimeProvider();
        var changed = Signal();
        var available = 1;
        await using var reader = new ProgressiveCachingStream(file,
            () => new(available, 100, false, false,
                changed.Task, Task.CompletedTask), () => { }, TimeSpan.FromMilliseconds(80),
            timeProvider: clock);
        reader.Position = 80;
        var pending = reader.ReadAsync(new byte[1]).AsTask();

        Assert.Equal(TimeSpan.FromMilliseconds(80), await clock.NextTimerAsync());
        for (var remaining = 60; remaining > 0; remaining -= 20)
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            var previous = changed;
            changed = Signal();
            previous.TrySetResult();
            Assert.Equal(TimeSpan.FromMilliseconds(remaining), await clock.NextTimerAsync());
        }

        clock.Advance(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task SeekAhead_RechecksPublishedBytesWhenTheNotificationDeadlineWins()
    {
        var payload = Enumerable.Range(0, 8).Select(index => (byte)index).ToArray();
        await using var file = FixtureFile(payload);
        var clock = new ManualTimeProvider();
        var snapshots = 0;
        await using var reader = new ProgressiveCachingStream(file,
            () => new(++snapshots >= 3 ? payload.Length : snapshots, payload.Length, false, false,
                NeverCompletingTask(), Task.CompletedTask), () => { }, TimeSpan.FromMilliseconds(10),
            timeProvider: clock);
        reader.Position = 7;
        snapshots = 0;

        var buffer = new byte[1];
        var pending = reader.ReadAsync(buffer).AsTask();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(10), await clock.NextTimerAsync());
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(payload[7], buffer[0]);
        Assert.Equal(3, snapshots);
    }

    [Fact]
    public async Task SeekFifteenSecondsAhead_WaitsWhileTheProducerKeepsAdvancing()
    {
        const int bytesPerSecond = 1024;
        var payload = Enumerable.Range(0, 17 * bytesPerSecond).Select(index => (byte)(index % 251)).ToArray();
        await using var file = FixtureFile(payload);
        var clock = new ManualTimeProvider();
        var available = bytesPerSecond;
        var publishing = false;
        ProgressiveCacheReadState State()
        {
            if (publishing)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                available += bytesPerSecond;
                Assert.True(available <= 16 * bytesPerSecond);
            }
            return new(available, payload.Length, false, false, Task.CompletedTask, Task.CompletedTask);
        }
        await using var reader = new ProgressiveCachingStream(file, State, () => { }, TimeSpan.FromSeconds(3),
            timeProvider: clock);
        reader.Position = 15 * bytesPerSecond;
        publishing = true;

        var buffer = new byte[32];
        Assert.Equal(buffer.Length, await reader.ReadAsync(buffer));
        Assert.Equal(payload[(15 * bytesPerSecond)..(15 * bytesPerSecond + buffer.Length)], buffer);
        Assert.Equal(16 * bytesPerSecond, available);
        Assert.Equal(TimeSpan.FromSeconds(15), clock.GetElapsedTime(0));
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task LongerInitialWaitAppliesOnlyWhileNoBytesHaveArrived()
    {
        await using var file = FixtureFile(new byte[100]);
        var clock = new ManualTimeProvider();
        var available = 0;
        var changed = Signal();
        await using var reader = new ProgressiveCachingStream(file,
            () => new(Volatile.Read(ref available), 100, false, false, changed.Task, Task.CompletedTask),
            () => { }, TimeSpan.FromMilliseconds(50), initialWaitTimeout: TimeSpan.FromSeconds(2), timeProvider: clock);
        var first = reader.ReadAsync(new byte[1]).AsTask();
        Assert.Equal(TimeSpan.FromSeconds(2), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.False(first.IsCompleted);
        Volatile.Write(ref available, 1);
        var previous = changed;
        changed = Signal();
        previous.TrySetResult();
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(30)));
        reader.Position = 80;
        var pending = reader.ReadAsync(new byte[1]).AsTask();
        Assert.Equal(TimeSpan.FromMilliseconds(50), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private static Task NeverCompletingTask() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
}
