using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolStreamingResponseAdapterTests
{
    [Fact]
    public async Task OrdinaryProviderPlaybackKeepsItsInitialPreparationWindow()
    {
        var clock = new ManualTimeProvider();
        var adapter = new ProtocolStreamingResponseAdapter(clock);
        var context = new DefaultHttpContext();
        using var source = new SeekSource();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        var interrupted = false;
        var result = Assert.IsType<FileStreamResult>(await adapter.CreateAsync(context, response, default, false,
            providerStream: true, interrupted: () => interrupted = true));
        await using var stream = result.FileStream;
        var pending = stream.ReadAsync(new byte[4]).AsTask();
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(60), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(52));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(interrupted);
    }

    [Fact]
    public async Task ClientCancelledPlaybackDoesNotMarkProviderFailure()
    {
        var context = new DefaultHttpContext();
        using var source = new SeekSource();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        var interrupted = false;
        var result = Assert.IsType<FileStreamResult>(await _adapter.CreateAsync(context, response, default, false,
            providerStream: true, interrupted: () => interrupted = true));
        await using var stream = result.FileStream;
        using var cancellation = new CancellationTokenSource();
        var pending = stream.ReadAsync(new byte[4], cancellation.Token).AsTask();
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(interrupted);
    }

    [Fact]
    public async Task ProviderSeekWaitsForItsFirstByteBeforePublishingRangeHeaders()
    {
        var clock = new ManualTimeProvider();
        var adapter = new ProtocolStreamingResponseAdapter(clock);
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=8-11";
        using var source = new SeekSource();
        using var response = PartialResponse(source);
        var pending = adapter.CreateAsync(context, response, default, false, providerStream: true);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.False(context.Response.HasStarted);
        Assert.Null(context.Response.ContentLength);
        Assert.Equal(0, context.Response.Headers.ContentRange.Count);
        Assert.Equal(TimeSpan.FromSeconds(8), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(context.Response.HasStarted);
        Assert.Equal(504, allstarr.Controllers.JellyfinController.MapExternalStreamException(new TimeoutException()).statusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderPartialResponsePreservesRangeLengthAndRejectsPrematureEof(bool shortBody)
    {
        using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Range = "bytes=8-11";
        context.Response.Body = new MemoryStream();
        using var response = PartialResponse(new MemoryStream(shortBody ? [8, 9] : [8, 9, 10, 11]));
        var result = await _adapter.CreateAsync(context, response, default, true, providerStream: true);
        var executing = result.ExecuteResultAsync(new ActionContext(context, new(), new()));
        if (shortBody) await Assert.ThrowsAsync<IOException>(() => executing);
        else
        {
            await executing;
            Assert.Equal(new byte[] { 8, 9, 10, 11 }, ((MemoryStream)context.Response.Body).ToArray());
        }
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal(4, context.Response.ContentLength);
        Assert.Equal("bytes 8-11/32", context.Response.Headers.ContentRange);
    }

    [Fact]
    public async Task ProviderReadDeadlineStopsAStallAfterItsFirstBytes()
    {
        var clock = new ManualTimeProvider();
        var adapter = new ProtocolStreamingResponseAdapter(clock);
        var context = new DefaultHttpContext();
        using var source = new SeekSource(firstByte: true);
        using var response = PartialResponse(source);
        var result = Assert.IsType<FileStreamResult>(await adapter.CreateAsync(context, response, default, false, providerStream: true));
        await using var stream = result.FileStream;
        Assert.Equal(1, await stream.ReadAsync(new byte[4]));
        var pending = stream.ReadAsync(new byte[4]).AsTask();
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(8), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static HttpResponseMessage PartialResponse(Stream source)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(source) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        response.Content.Headers.ContentLength = 4;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(8, 11, 32);
        return response;
    }

    private sealed class SeekSource(bool firstByte = false) : Stream
    {
        public readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (firstByte && Interlocked.Increment(ref reads) == 1) { buffer.Span[0] = 8; return 1; }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
