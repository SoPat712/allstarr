using System.Net;
using System.Net.Http.Headers;

namespace allstarr.Tests;

public sealed partial class ExtensionProgressivePlaybackTests
{
    [Theory]
    [InlineData(true, "audio/flac")]
    [InlineData(false, "audio/flac")]
    [InlineData(true, "application/octet-stream")]
    public async Task Playback_DirectFlacWithCleanupReadsAnImmutablePrefixBeforeCompletion(bool knownLength, string mimeType)
    {
        "fLaC"u8.CopyTo(prefix);
        prefix[4] = 128;
        prefix[5] = prefix[6] = 0;
        prefix[7] = 34;
        var (adapter, context, track) = Setup(knownLength,
            "if (!file) throw new Error('missing files'); if (typeof file.delete === 'function') file.delete('unused');",
            mimeType: mimeType, extension: ".flac");
        Assert.False(sandbox.AllowsProgressiveDownload);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Equal(knownLength ? prefix.Length + 4 : null, response.Content.Headers.ContentLength);
        using var body = await response.Content.ReadAsStreamAsync();
        var actual = new byte[prefix.Length];
        await body.ReadExactlyAsync(actual).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(prefix, actual);
        Assert.False(StreamProducerCompletion(adapter).IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        var tail = new byte[4];
        await body.ReadExactlyAsync(tail).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, tail);
        Assert.Equal(0, await body.ReadAsync(new byte[1]));
        var next = (await adapter.GetStreamLeaseAsync(context, new(track))).RequireValue();
        using var seek = new HttpRequestMessage(HttpMethod.Get, next.ProtectedSourceUri);
        seek.Headers.Range = new RangeHeaderValue(prefix.Length, null);
        using var resumed = await next.ProtectedResponseFactory!(seek, default);
        Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await resumed.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("file.delete(path);")]
    [InlineData("try { file.delete(path); } catch(e) {}")]
    public async Task Playback_DeletingThePublishedFlacRejectsTheArtifactAndFailsTheReader(string processing)
    {
        "fLaC"u8.CopyTo(prefix);
        prefix[4] = 128;
        prefix[5] = prefix[6] = 0;
        prefix[7] = 34;
        var (adapter, context, track) = Setup(true, processing, extension: ".flac");
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[prefix.Length]);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        await StreamProducerCompletion(adapter).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => body.CopyToAsync(Stream.Null));
        await adapter.DisposeAsync();
        Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("file.delete('unused');", "audio/mp4", ".m4a")]
    [InlineData("file.writeBytes(path,'AQIDBA==');", "audio/flac", ".flac")]
    public async Task Playback_CleanupCaptureNeverPublishesMp4OrWritableScripts(string processing, string mimeType, string extension)
    {
        "fLaC"u8.CopyTo(prefix);
        prefix[4] = 128;
        prefix[5] = prefix[6] = 0;
        prefix[7] = 34;
        var (adapter, context, track) = Setup(true, processing, mimeType: mimeType, extension: extension);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
    }
}
