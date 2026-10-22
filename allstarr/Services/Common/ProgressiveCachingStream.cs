namespace allstarr.Services.Common;

internal readonly record struct ProgressiveCacheReadState(
    long AvailableLength,
    long? TotalLength,
    bool Finished,
    bool Failed,
    Task Changed,
    Task Completion);

/// <summary>
/// Reads a cache file while its owner publishes availability and completion state.
/// </summary>
public sealed class ProgressiveCachingStream : Stream
{
    internal static readonly TimeSpan SeekWaitTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan InitialReadWaitTimeout = TimeSpan.FromSeconds(60);
    private readonly Stream source;
    private readonly Func<ProgressiveCacheReadState> readState;
    private readonly Action closed;
    private readonly Action? started;
    private readonly TimeSpan waitTimeout;
    private readonly TimeSpan initialWaitTimeout;
    private readonly TimeSpan? maximumWaitTimeout;
    private readonly TimeProvider timeProvider;
    private long position;
    private int startedInvoked;
    private int disposed;

    internal ProgressiveCachingStream(
        Stream file,
        Func<ProgressiveCacheReadState> state,
        Action closed,
        TimeSpan waitTimeout,
        Action? started = null,
        TimeSpan? initialWaitTimeout = null,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumWaitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(closed);
        if (waitTimeout < TimeSpan.Zero || waitTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        if (initialWaitTimeout is { } initial && (initial < TimeSpan.Zero || initial == Timeout.InfiniteTimeSpan))
            throw new ArgumentOutOfRangeException(nameof(initialWaitTimeout));

        if (maximumWaitTimeout is { } maximum && (maximum <= TimeSpan.Zero || maximum == Timeout.InfiniteTimeSpan))
            throw new ArgumentOutOfRangeException(nameof(maximumWaitTimeout));

        source = file;
        readState = state;
        this.closed = closed;
        this.waitTimeout = waitTimeout;
        this.initialWaitTimeout = initialWaitTimeout ?? waitTimeout;
        this.started = started;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maximumWaitTimeout = maximumWaitTimeout;
    }

    public override bool CanRead => Volatile.Read(ref disposed) == 0 && source.CanRead;

    public override bool CanSeek =>
        Volatile.Read(ref disposed) == 0 && source.CanSeek && TryGetTotalLength(out _);

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            if (!TryGetTotalLength(out var length))
                throw new NotSupportedException();
            return length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        ReadReaderAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadReaderAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        InvokeStarted();
        var waitingSince = timeProvider.GetTimestamp();
        var waitingStarted = waitingSince;
        long observedLength = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = readState();
            if (snapshot.Failed || snapshot.AvailableLength < 0 || snapshot.TotalLength is < 0 ||
                snapshot.TotalLength is { } total && snapshot.AvailableLength > total)
                throw FailedTransfer();
            if (snapshot.AvailableLength > observedLength)
            {
                observedLength = snapshot.AvailableLength;
                waitingSince = timeProvider.GetTimestamp();
            }
            var available = snapshot.AvailableLength - position;
            if (available > 0)
            {
                var count = (int)Math.Min(available, buffer.Length);
                try
                {
                    source.Position = position;
                    var read = await source.ReadAsync(buffer[..count], cancellationToken);
                    if (read <= 0 || read > count) throw FailedTransfer();
                    position += read;
                    return read;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { throw FailedTransfer(); }
            }
            if (snapshot.Finished)
            {
                await AwaitCompletionAsync(snapshot.Completion, cancellationToken);
                return 0;
            }
            var timeout = observedLength == 0 ? initialWaitTimeout : waitTimeout;
            var remaining = timeout - timeProvider.GetElapsedTime(waitingSince);
            if (maximumWaitTimeout is { } maximum && (position > 0 || observedLength > 0))
            {
                var budget = maximum - timeProvider.GetElapsedTime(waitingStarted);
                if (budget < remaining) remaining = budget;
            }
            await WaitForChangeAsync(snapshot.Changed, remaining, cancellationToken);
        }
    }

    internal async Task WaitForRangeAsync(System.Net.Http.Headers.RangeHeaderValue range, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (range.Ranges.Single().From == 0) return;
        InvokeStarted();
        using var timeout = new CancellationTokenSource(SeekWaitTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (true)
            {
                var snapshot = readState();
                if (snapshot.Failed) throw FailedTransfer();
                if (snapshot.TotalLength is { } total)
                {
                    if (!StreamByteRanges.TryResolve(range, total, out var start, out _) || snapshot.AvailableLength > start)
                        return;
                }
                if (snapshot.Finished) throw FailedTransfer();
                await snapshot.Changed.WaitAsync(linked.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The progressive media cache did not reach the requested position before the seek timeout.");
        }
    }

    private bool TryGetTotalLength(out long length)
    {
        length = 0;
        if (Volatile.Read(ref disposed) != 0)
            return false;
        var total = readState().TotalLength;
        if (total is not { } value || value < 0)
            return false;
        length = value;
        return true;
    }

    private async ValueTask WaitForChangeAsync(
        Task changed,
        TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
            throw new IOException("The progressive media cache did not advance before the read timeout.");
        try
        {
            await changed.WaitAsync(remaining, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            // The producer may have advanced before this continuation was scheduled.
            // Recheck published state before deciding whether the stall budget expired.
        }
        catch
        {
            throw FailedTransfer();
        }
    }

    private static async ValueTask AwaitCompletionAsync(
        Task completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw FailedTransfer();
        }
    }

    private void InvokeStarted()
    {
        if (Interlocked.Exchange(ref startedInvoked, 1) == 0)
            started?.Invoke();
    }

    private long RequireTotalLength()
    {
        ThrowIfDisposed();
        if (!source.CanSeek || !TryGetTotalLength(out var length))
            throw new NotSupportedException();
        return length;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(ProgressiveCachingStream));
    }

    private static IOException FailedTransfer() =>
        new("The progressive media transfer failed.");

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var total = RequireTotalLength();
        long target;
        try
        {
            target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(total + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (target < 0 || target > total)
            throw new ArgumentOutOfRangeException(nameof(offset));
        try
        {
            source.Seek(target, SeekOrigin.Begin);
        }
        catch
        {
            throw FailedTransfer();
        }

        position = target;
        return position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        if (disposing)
        {
            try
            {
                source.Dispose();
            }
            finally
            {
                closed();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try
        {
            await source.DisposeAsync();
        }
        finally
        {
            closed();
            GC.SuppressFinalize(this);
        }
    }

}
