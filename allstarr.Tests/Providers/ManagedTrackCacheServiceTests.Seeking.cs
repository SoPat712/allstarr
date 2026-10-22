using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ByteZeroPlaybackCanOpenWhileTheSharedFillIsIncomplete(bool knownLength)
    {
        await using var fixture = new ProgressiveFixture(knownLength);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var response = (await fixture.OpenAsync())!.Response;
        var http = new DefaultHttpContext();
        http.Request.Headers.Range = "bytes=0-";
        var opening = new ProtocolStreamingResponseAdapter().CreateAsync(http, response, default, true);
        Assert.True(opening.IsCompletedSuccessfully);
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.FileStreamResult>(await opening);
        await using var reader = result.FileStream;
        Assert.Equal(knownLength, reader.CanSeek);
        Assert.False(fixture.Source.Release.Task.IsCompleted);
        Assert.False(http.Response.HasStarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledMediaFactObserverCannotStrandCompletedRangeOrPublication(bool knownLength)
    {
        await using var fixture = new ProgressiveFixture(knownLength);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.StartAsync((_, _) =>
        {
            observed.TrySetResult();
            return Task.FromCanceled(new CancellationToken(true));
        });
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Response.Dispose();
        using var range = (await fixture.OpenAsync())!.Response;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var serving = ExecuteAsync(range, "bytes=10-", cancellationToken: cancellation.Token);
            fixture.Source.Release.TrySetResult();
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var (http, bytes) = await serving.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(206, http.Response.StatusCode);
            Assert.Equal("bytes 10-15/16", http.Response.Headers.ContentRange.ToString());
            Assert.Equal(6, http.Response.ContentLength);
            Assert.Equal(fixture.Bytes[10..], bytes);
            Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.PublishedPath!));
        }
        finally { cancellation.Cancel(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FarSeekReadinessTimesOutBeforeCommittingAResponse(bool knownLength)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength, timeProvider: clock);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var range = (await fixture.OpenAsync())!.Response;
        using var cancellation = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = cancellation.Token };
        http.Request.Method = "GET";
        http.Request.Headers.Range = "bytes=10-";
        try
        {
            var opening = new ProtocolStreamingResponseAdapter().CreateAsync(http, range, cancellation.Token, true);
            Assert.False(opening.IsCompleted);
            await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
            clock.Advance(TimeSpan.FromSeconds(8));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(http.Response.HasStarted);
            Assert.Empty(http.Response.Headers.ContentRange.ToString());
            Assert.Equal(504, allstarr.Controllers.JellyfinController.MapExternalStreamException(error).statusCode);
            Assert.Equal(504, allstarr.Filters.SubsonicExceptionFilter.Map(error).StatusCode);
        }
        finally { cancellation.Cancel(); }
    }

    [Fact]
    public async Task TwoFarRangesContinueAfterTheInitialClientAborts()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        using var firstRequest = new CancellationTokenSource();
        await fixture.StartAsync((_, _) => Task.FromCanceled(firstRequest.Token));
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        firstRequest.Cancel();
        fixture.Response.Dispose();
        using var first = (await fixture.OpenAsync())!.Response;
        using var second = (await fixture.OpenAsync())!.Response;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var one = ExecuteAsync(first, "bytes=10-11", cancellationToken: cancellation.Token);
            var two = ExecuteAsync(second, "bytes=12-15", cancellationToken: cancellation.Token);
            Assert.False(one.IsCompleted);
            Assert.False(two.IsCompleted);
            Assert.False(fixture.Source.Closed);
            fixture.Source.Release.TrySetResult();
            Assert.Equal(fixture.Bytes[10..12], (await one.WaitAsync(TimeSpan.FromSeconds(10))).Bytes);
            Assert.Equal(fixture.Bytes[12..], (await two.WaitAsync(TimeSpan.FromSeconds(10))).Bytes);
            await fixture.Published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { cancellation.Cancel(); }
    }
}
