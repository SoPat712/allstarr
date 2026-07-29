using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ProgressiveCachingStreamTests
{
    [Fact]
    public async Task ReadAsync_RelaysExactBytesAndPublishesCacheOnlyAtEof()
    {
        var bytes = Enumerable.Range(0, 32_777).Select(value => (byte)(value % 251)).ToArray();
        await using var cache = new MemoryStream();
        var completed = false;
        var aborted = false;
        await using var stream = new ProgressiveCachingStream(
            new MemoryStream(bytes, writable: false),
            cache,
            "audio/flac",
            () =>
            {
                completed = true;
                return Task.CompletedTask;
            },
            () => aborted = true);

        await using var received = new MemoryStream();
        await stream.CopyToAsync(received);

        Assert.Equal(bytes, received.ToArray());
        Assert.Equal(bytes, cache.ToArray());
        Assert.Equal("audio/flac", stream.ContentType);
        Assert.False(stream.CanSeek);
        Assert.True(completed);
        Assert.False(aborted);
    }

    [Fact]
    public async Task DisposeBeforeEof_AbortsWithoutPublishingPartialCache()
    {
        var completed = false;
        var aborted = false;
        var stream = new ProgressiveCachingStream(
            new MemoryStream(new byte[100], writable: false),
            new MemoryStream(),
            "audio/flac",
            () =>
            {
                completed = true;
                return Task.CompletedTask;
            },
            () => aborted = true);

        var buffer = new byte[10];
        Assert.Equal(10, await stream.ReadAsync(buffer));
        await stream.DisposeAsync();

        Assert.False(completed);
        Assert.True(aborted);
    }
}
