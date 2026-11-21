using System.Text.Json;
using allstarr.Core.Playback;

namespace allstarr.Middleware;

public sealed class PlaybackStartTimingMiddleware(RequestDelegate next, PlaybackStartTimingStore store, TimeProvider clock)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var timing = new PlaybackStartTimings(clock);
        timing.Enter();
        var original = context.Response.Body;
        var body = new TimingBody(original, timing, store, context.Response);
        context.Response.Body = body;
        context.Response.OnStarting(() => { if (timing.Enabled) timing.Headers(); return Task.CompletedTask; });
        var outcome = "completed";
        var escaped = false;
        try
        {
            await next(context);
            if (context.RequestAborted.IsCancellationRequested || context.Response.StatusCode == 499) outcome = "cancelled";
            else if (context.Response.StatusCode >= 400) outcome = "failed";
        }
        catch (OperationCanceledException) { outcome = "cancelled"; escaped = true; throw; }
        catch { outcome = "failed"; escaped = true; throw; }
        finally
        {
            context.Response.Body = original;
            if (timing.Enabled)
            {
                // An error that escapes is answered further out, so its status is known only once headers went out.
                timing.Answer(!escaped || context.Response.HasStarted ? context.Response.StatusCode : null, body.ErrorCode());
                var final = timing.Snapshot(outcome, complete: true);
                try { await timing.PendingRecords.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                await store.RecordAsync(final);
            }
        }
    }

    private sealed class TimingBody(Stream source, PlaybackStartTimings timing, PlaybackStartTimingStore store, HttpResponse response) : Stream
    {
        private const int ErrorBodyLimit = 4096;
        private byte[]? errorBody;
        private int errorLength;
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
            if (!buffer.IsEmpty && timing.Enabled && response.StatusCode >= 400 && errorLength < ErrorBodyLimit)
            {
                errorBody ??= new byte[ErrorBodyLimit];
                var take = Math.Min(buffer.Length, ErrorBodyLimit - errorLength);
                buffer.Span[..take].CopyTo(errorBody.AsSpan(errorLength));
                errorLength += take;
            }
            // The rest of the audio must not wait for this record either.
            if (!buffer.IsEmpty && timing.Enabled && timing.FirstByte())
                timing.Recording(store.RecordInBackground(timing.Snapshot(PlaybackStartTimingStore.StartedOutcome)));
        }

        /// <summary>The top-level <c>code</c> of a JSON error body, the code the client was given.</summary>
        public string? ErrorCode()
        {
            if (errorBody == null) return null;
            var reader = new Utf8JsonReader(errorBody.AsSpan(0, errorLength), isFinalBlock: false, state: default);
            try
            {
                while (reader.Read())
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("code"))
                        return reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            catch (JsonException) { }
            return null;
        }
    }
}
