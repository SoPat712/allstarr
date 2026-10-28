using System.Net;
using System.Net.Http.Headers;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

internal sealed class ProviderPlaybackStream(Stream source, HttpContent owner, long start, long? length,
    long? totalLength, long? rangeEnd, EntityTagHeaderValue? entityTag,
    Func<long, long?, EntityTagHeaderValue, CancellationToken, Task<HttpResponseMessage>>? reopen,
    Action failed, TimeProvider clock) : Stream
{
    private long delivered;
    private int resumeAttempts;
    private bool reported;
    private HttpResponseMessage? replacement;
    private Stream current = source;

    internal static async Task AttachAsync(HttpResponseMessage response, bool supportsByteRanges,
        Func<long, long?, EntityTagHeaderValue, CancellationToken, Task<HttpResponseMessage>> reopen,
        Action failed, TimeProvider clock, CancellationToken cancellationToken)
    {
        var owner = response.Content;
        var source = await owner.ReadAsStreamAsync(cancellationToken);
        var range = owner.Headers.ContentRange;
        var length = owner.Headers.ContentLength ?? (range is { From: { } from, To: { } to } ? to - from + 1 : null);
        var tag = response.Headers.ETag is { IsWeak: false } strong ? strong : null;
        var stream = new ProviderPlaybackStream(source, owner, range?.From ?? 0, length,
            range?.Length ?? owner.Headers.ContentLength, range?.To, tag,
            supportsByteRanges && tag != null ? reopen : null, failed, clock);
        PrefetchedStream.ReplaceContent(response, stream, PrefetchedStream.Facts(owner));
    }

    public override bool CanRead => current.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || length == delivered) return 0;
        if (length is { } expected && expected - delivered < buffer.Length)
            buffer = buffer[..(int)(expected - delivered)];
        while (true)
        {
            using var timeout = new CancellationTokenSource(ProgressiveCachingStream.SeekWaitTimeout, clock);
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                var count = await ReadBoundedAsync(current, buffer, reading.Token);
                if (count == 0 && length is { } size && delivered < size)
                    throw new IOException("The provider media ended before its declared length.");
                delivered += count;
                return count;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                error is IOException or HttpRequestException or OperationCanceledException)
            {
                current.Dispose();
                if (reopen == null || totalLength == null || entityTag == null)
                    throw Stopped();
                var resumed = false;
                while (resumeAttempts < 3)
                {
                    var attempt = resumeAttempts++;
                    if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), clock, cancellationToken);
                    using var reconnectTimeout = new CancellationTokenSource(ProgressiveCachingStream.SeekWaitTimeout, clock);
                    using var reconnect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, reconnectTimeout.Token);
                    HttpResponseMessage? response = null;
                    try
                    {
                        var offset = checked(start + delivered);
                        response = await reopen(offset, rangeEnd, entityTag, reconnect.Token);
                        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
                            (int)response.StatusCode >= 500) continue;
                        var range = response.Content.Headers.ContentRange;
                        if (response.StatusCode != HttpStatusCode.PartialContent || !entityTag.Equals(response.Headers.ETag) ||
                            range == null || range.From != offset || range.To != (rangeEnd ?? totalLength - 1) || range.Length != totalLength ||
                            !StreamByteRanges.HasValidContentRange(range, response.Content.Headers.ContentLength)) break;
                        var next = await response.Content.ReadAsStreamAsync(reconnect.Token);
                        replacement?.Dispose();
                        replacement = response;
                        current = next;
                        response = null;
                        resumed = true;
                        break;
                    }
                    catch (Exception reconnectError) when (!cancellationToken.IsCancellationRequested &&
                        reconnectError is IOException or HttpRequestException or OperationCanceledException)
                    { }
                    finally { response?.Dispose(); }
                }
                if (!resumed) throw Stopped();
            }
        }
    }

    private IOException Stopped()
    {
        if (!reported) { reported = true; failed(); }
        return new IOException("The selected playback source could not provide more identical audio bytes.");
    }

    private static async Task<int> ReadBoundedAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(destination.Length, 81920));
        Task<int>? pending = null;
        try
        {
            pending = source.ReadAsync(buffer.AsMemory(0, Math.Min(destination.Length, buffer.Length)), cancellationToken).AsTask();
            var count = await pending.WaitAsync(cancellationToken);
            buffer.AsMemory(0, count).CopyTo(destination);
            return count;
        }
        finally
        {
            // A source ignoring cancellation must never keep writing into the client's next read buffer.
            if (pending is { IsCompleted: false })
                _ = pending.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { replacement?.Dispose(); owner.Dispose(); }
        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
