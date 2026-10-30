using Microsoft.AspNetCore.Mvc;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed class ProtocolStreamingResponseAdapter(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly string[] ForwardedResponseHeaders =
    [
        "Accept-Ranges",
        "Content-Range",
        "Content-Length",
        "ETag",
        "Last-Modified",
        "Cache-Control",
        "X-Allstarr-Provider",
        "X-Allstarr-Route-Reason",
        "X-Allstarr-Audio-Codec",
        "X-Allstarr-Audio-Bitrate",
        "X-Allstarr-Audio-Container",
        "X-Allstarr-Start-Seconds",
        "X-Content-Duration"
    ];

    public void ForwardRangeRequestHeaders(
        IHeaderDictionary incomingHeaders,
        HttpRequestMessage request)
    {
        ForwardHeader(incomingHeaders, request, "Range");
        ForwardHeader(incomingHeaders, request, "If-Range");
    }

    public async Task<IActionResult> CreateAsync(
        HttpContext context,
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        bool enableRangeProcessing,
        bool providerStream = false,
        IDisposable? responseLease = null,
        Action? interrupted = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            return new StatusCodeResult(statusCode);
        }

        context.Response.RegisterForDispose(response);
        if (responseLease != null)
        {
            context.Response.RegisterForDispose(responseLease);
            context.Response.RegisterForDispose(context.RequestAborted.Register(responseLease.Dispose));
            context.Response.OnCompleted(() => { responseLease.Dispose(); return Task.CompletedTask; });
        }
        var headOnly = HttpMethods.IsHead(context.Request.Method);
        var partial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var seeking = providerStream && (response.Content.Headers.ContentRange?.From is > 0 ||
            StreamByteRanges.Parse(context.Request.Headers.Range) is { } requested && requested.Ranges.Single().From != 0);
        if (providerStream && partial && response.Content.Headers.ContentRange is { From: { } from, To: { } to })
            response.Content.Headers.ContentLength ??= to - from + 1;
        if (!headOnly)
        {
            if (enableRangeProcessing && response.StatusCode == System.Net.HttpStatusCode.OK)
                await ManagedTrackCacheService.WaitForRangeAsync(response.Content, context.Request.Headers.Range, cancellationToken);
            if (providerStream && partial)
            {
                using var timeout = new CancellationTokenSource(seeking ? ProgressiveCachingStream.SeekWaitTimeout :
                    ProgressiveCachingStream.InitialReadWaitTimeout, clock);
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    if (!await PrefetchedStream.PrepareAsync(response, reading.Token).WaitAsync(reading.Token))
                        throw new IOException("The provider media ended before its declared range.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The provider did not deliver the requested position before the seek timeout.");
                }
            }
        }
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in ForwardedResponseHeaders)
        {
            if (response.Headers.TryGetValues(header, out var values) ||
                response.Content.Headers.TryGetValues(header, out values))
            {
                context.Response.Headers[header] = values.ToArray();
            }
        }

        if (headOnly)
        {
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
            return new EmptyResult();
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new FileStreamResult(
            new StartingResponseStream(stream, context.Response, clock, providerStream, seeking,
                providerStream && partial ? response.Content.Headers.ContentLength : null, providerStream && partial, responseLease, interrupted),
            response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg")
        {
            EnableRangeProcessing = enableRangeProcessing && !(providerStream && partial)
        };
    }

    public static IActionResult CreateTransportFailure(
        CancellationToken requestAborted,
        Exception exception,
        string message)
    {
        if (requestAborted.IsCancellationRequested && exception is OperationCanceledException)
        {
            return new StatusCodeResult(499);
        }

        return new ObjectResult(new { error = message })
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
    }

    private static void ForwardHeader(
        IHeaderDictionary incomingHeaders,
        HttpRequestMessage request,
        string name)
    {
        if (incomingHeaders.TryGetValue(name, out var values))
        {
            request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }

    private sealed class StartingResponseStream(Stream source, HttpResponse response, TimeProvider clock,
        bool providerStream, bool seeking, long? remaining, bool partial, IDisposable? capacityLease, Action? interrupted) : Stream
    {
        private bool initialRead = true;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => !partial && source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return await ReadCoreAsync(buffer, cancellationToken); }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested && !response.HttpContext.RequestAborted.IsCancellationRequested)
                    interrupted?.Invoke();
                throw;
            }
        }
        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.IsEmpty) return 0;
            await response.StartAsync(cancellationToken);
            if (remaining == 0) return 0;
            if (remaining is { } length && length < buffer.Length) buffer = buffer[..(int)length];
            int read;
            if (providerStream)
            {
                using var timeout = new CancellationTokenSource(!seeking && initialRead ? ProgressiveCachingStream.InitialReadWaitTimeout :
                    ProgressiveCachingStream.SeekWaitTimeout, clock);
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try { read = await source.ReadAsync(buffer, reading.Token).AsTask().WaitAsync(reading.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new IOException("The provider media did not advance before the read timeout."); }
            }
            else read = await source.ReadAsync(buffer, cancellationToken);
            initialRead = false;
            if (remaining is { } expected)
            {
                if (read == 0) throw new IOException("The provider media ended before its declared range.");
                remaining = expected - read;
            }
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { source.Dispose(); }
                finally { capacityLease?.Dispose(); }
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            try { await source.DisposeAsync(); }
            finally { capacityLease?.Dispose(); GC.SuppressFinalize(this); }
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
