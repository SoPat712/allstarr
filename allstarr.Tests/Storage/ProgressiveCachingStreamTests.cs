using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ProgressiveCachingStreamTests
{
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
        await using var reader = new ProgressiveCachingStream(
            file,
            () => new(0, null, false, false, NeverCompletingTask(), NeverCompletingTask()),
            static () => { },
            TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1].AsMemory()).AsTask());
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
        using var canceled = new CancellationTokenSource();
        var available = 1;
        await using var reader = new ProgressiveCachingStream(file,
            () => new(available, 100, false, false,
                Task.Delay(10, canceled.Token), Task.CompletedTask), () => { }, TimeSpan.FromMilliseconds(80));
        reader.Position = 80;
        var pending = reader.ReadAsync(new byte[1], canceled.Token).AsTask();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromMilliseconds(350)));
        }
        finally
        {
            canceled.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task SeekFifteenSecondsAhead_WaitsWhileTheProducerKeepsAdvancing()
    {
        const int bytesPerSecond = 1024;
        var payload = Enumerable.Range(0, 17 * bytesPerSecond).Select(index => (byte)(index % 251)).ToArray();
        await using var file = FixtureFile(payload);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var gate = new object();
        var available = bytesPerSecond;
        var changed = Signal();
        ProgressiveCacheReadState State()
        {
            lock (gate) return new(available, payload.Length, false, false, changed.Task, Task.CompletedTask);
        }
        await using var reader = new ProgressiveCachingStream(file, State, () => { }, TimeSpan.FromSeconds(3));
        reader.Position = 15 * bytesPerSecond;
        var producer = Task.Run(async () =>
        {
            for (var seconds = 1; seconds <= 15; seconds++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                TaskCompletionSource previous;
                lock (gate)
                {
                    available += bytesPerSecond;
                    previous = changed;
                    changed = Signal();
                }
                previous.TrySetResult();
            }
        }, cancellation.Token);
        try
        {
            var buffer = new byte[32];
            Assert.Equal(buffer.Length, await reader.ReadAsync(buffer, cancellation.Token));
            Assert.Equal(payload[(15 * bytesPerSecond)..(15 * bytesPerSecond + buffer.Length)], buffer);
            Assert.Equal(16 * bytesPerSecond, available);
        }
        finally
        {
            cancellation.Cancel();
            try { await producer; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task LongerInitialWaitAppliesOnlyWhileNoBytesHaveArrived()
    {
        await using var file = FixtureFile(new byte[100]);
        var available = 0;
        var changed = Signal();
        await using var reader = new ProgressiveCachingStream(file,
            () => new(Volatile.Read(ref available), 100, false, false, changed.Task, Task.CompletedTask),
            () => { }, TimeSpan.FromMilliseconds(50), initialWaitTimeout: TimeSpan.FromSeconds(2));
        var first = reader.ReadAsync(new byte[1]).AsTask();
        await Task.Delay(150);
        Assert.False(first.IsCompleted);
        Volatile.Write(ref available, 1);
        var previous = changed;
        changed = Signal();
        previous.TrySetResult();
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(1)));
        reader.Position = 80;
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1]).AsTask()
            .WaitAsync(TimeSpan.FromMilliseconds(500)));
    }

    private static Task NeverCompletingTask() =>
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
}
