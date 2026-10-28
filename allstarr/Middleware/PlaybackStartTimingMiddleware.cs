using allstarr.Core.Playback;

namespace allstarr.Middleware;

public sealed class PlaybackStartTimingMiddleware(RequestDelegate next, PlaybackStartTimingStore store, TimeProvider clock)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var timing = new PlaybackStartTimings(clock);
        timing.Enter();
        var original = context.Response.Body;
        context.Response.Body = new TimingBody(original, timing, store);
        context.Response.OnStarting(() => { if (timing.Enabled) timing.Headers(); return Task.CompletedTask; });
        var outcome = "completed";
        try
        {
            await next(context);
            if (context.RequestAborted.IsCancellationRequested || context.Response.StatusCode == 499) outcome = "cancelled";
            else if (context.Response.StatusCode >= 400) outcome = "failed";
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch { outcome = "failed"; throw; }
        finally
        {
            context.Response.Body = original;
            if (timing.Enabled) await store.RecordAsync(timing.Snapshot(outcome, complete: true));
        }
    }

    private sealed class TimingBody(Stream source, PlaybackStartTimings timing, PlaybackStartTimingStore store) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => source.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => source.Flush();
        public override Task FlushAsync(CancellationToken token) => source.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await source.WriteAsync(buffer, token);
            if (!buffer.IsEmpty && timing.Enabled && timing.FirstByte())
                await store.RecordAsync(timing.Snapshot("started"));
        }
    }
}
