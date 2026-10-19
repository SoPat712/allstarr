using Microsoft.AspNetCore.Mvc;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed class ProtocolStreamingResponseAdapter
{
    private static readonly string[] ForwardedResponseHeaders =
    [
        "Accept-Ranges",
        "Content-Range",
        "Content-Length",
        "ETag",
        "Last-Modified",
        "Cache-Control",
        "X-Allstarr-Provider"
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
        bool enableRangeProcessing)
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
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in ForwardedResponseHeaders)
        {
            if (response.Headers.TryGetValues(header, out var values) ||
                response.Content.Headers.TryGetValues(header, out values))
            {
                context.Response.Headers[header] = values.ToArray();
            }
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
            return new EmptyResult();
        }

        if (enableRangeProcessing && response.StatusCode == System.Net.HttpStatusCode.OK)
            await ManagedTrackCacheService.WaitForRangeLengthAsync(response.Content, context.Request.Headers.Range, cancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new FileStreamResult(
            new StartingResponseStream(stream, context.Response),
            response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg")
        {
            EnableRangeProcessing = enableRangeProcessing
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

    private sealed class StartingResponseStream(Stream source, HttpResponse response) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
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
            if (buffer.IsEmpty) return 0;
            await response.StartAsync(cancellationToken);
            return await source.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync() { await source.DisposeAsync(); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
