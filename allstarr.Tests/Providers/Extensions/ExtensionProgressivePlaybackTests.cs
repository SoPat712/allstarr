using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionProgressivePlaybackTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-progressive", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new(DateTimeOffset.UtcNow);
    private SteppedStream source = new();
    private readonly byte[] prefix = Enumerable.Repeat((byte)42, 256 * 1024).ToArray();
    private ProviderDownloadWorkspaceOptions options = null!;
    private int requests;
    private ExtensionSandbox sandbox = null!;
    private readonly List<ExtensionDownloadStreamingCapabilityAdapter> adapters = [];
    private readonly PlaybackLogger logger = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_DisposingAdapterStopsDownloadAndRemovesWorkspace(bool disposeBody)
    {
        var (adapter, context, track) = Setup(true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        if (disposeBody) body.Dispose();
        await adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetDirectories(options.RootPath));
        await adapter.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playback_ReadsBeforeCompletionAndWaitsForTheNextBytes(bool knownLength)
    {
        var (adapter, context, track) = Setup(knownLength);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        Assert.True(lease.SupportsSeeking);
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(knownLength ? prefix.Length + 4 : null, response.Content.Headers.ContentLength);
        using var body = await response.Content.ReadAsStreamAsync();
        var bytes = new byte[prefix.Length];
        await body.ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(prefix, bytes);
        var next = body.ReadAsync(new byte[4]).AsTask();
        Assert.False(next.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        var count = await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(count, 1, 4);
        await body.ReadExactlyAsync(new byte[4 - count]);
        Assert.Equal(0, await body.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task Playback_ConcurrentListenersShareTransferAndFutureRangeWaits()
    {
        var (adapter, context, track) = Setup(true);
        var first = adapter.GetStreamLeaseAsync(context, new(track));
        var equivalentPolicy = new ProviderExecutionPolicy(context.Policy.Quality, context.Policy.ExplicitContent,
            context.Policy.AllowFallback, context.Policy.AllowSharedAccount, context.Policy.AllowManagedDownloads, context.Policy.AllowedProviderIds);
        var equivalentContext = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, equivalentPolicy,
            context.OperationId, context.CorrelationId, context.Deadline, context.CancellationToken, context.IdempotencyKey);
        var second = adapter.GetStreamLeaseAsync(equivalentContext, new(track));
        source.Release(prefix);
        var leases = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, requests);
        using var request = new HttpRequestMessage(HttpMethod.Get, leases[0].RequireValue().ProtectedSourceUri);
        request.Headers.Range = new RangeHeaderValue(prefix.Length, prefix.Length + 3);
        using var response = await leases[0].RequireValue().ProtectedResponseFactory!(request, default);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes {prefix.Length}-{prefix.Length + 3}/{prefix.Length + 4}", response.Content.Headers.ContentRange!.ToString());
        var reading = response.Content.ReadAsByteArrayAsync();
        Assert.False(reading.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await reading.WaitAsync(TimeSpan.FromSeconds(10)));
        using var otherRequest = new HttpRequestMessage(HttpMethod.Head, leases[1].RequireValue().ProtectedSourceUri);
        using var other = await leases[1].RequireValue().ProtectedResponseFactory!(otherRequest, default);
    }

    [Fact]
    public async Task Playback_SeekAfterClosingFirstResponseReusesPublishedDownload()
    {
        var (adapter, context, track) = Setup(true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var first = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var initial = new HttpRequestMessage(HttpMethod.Get, first.ProtectedSourceUri);
        var response = await first.ProtectedResponseFactory!(initial, default);
        response.Dispose();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));
        var policy = new ProviderExecutionPolicy(context.Policy.Quality, context.Policy.ExplicitContent,
            context.Policy.AllowFallback, context.Policy.AllowSharedAccount, context.Policy.AllowManagedDownloads,
            [context.ProviderId, "qobuz"]);
        var resumedContext = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, policy,
            context.OperationId, context.CorrelationId, context.Deadline, context.CancellationToken, context.IdempotencyKey);
        var second = (await adapter.GetStreamLeaseAsync(resumedContext, new(track)).WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var seek = new HttpRequestMessage(HttpMethod.Get, second.ProtectedSourceUri);
        seek.Headers.Range = new RangeHeaderValue(prefix.Length, null);
        using var resumed = await second.ProtectedResponseFactory!(seek, default);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(source.Canceled.Task.IsCompleted);
        var reading = resumed.Content.ReadAsByteArrayAsync();
        Assert.False(reading.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await reading.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_TransferFailureOrReadDeadlineFailsTheListener(bool timeout)
    {
        var (adapter, context, track) = Setup(true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[prefix.Length]);
        var reading = body.ReadAsync(new byte[1]).AsTask();
        if (timeout) clock.Advance(TimeSpan.FromSeconds(31));
        else source.Fail();
        await Assert.ThrowsAnyAsync<IOException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
        source.Complete();
    }

    [Theory]
    [InlineData("var data=file.readBytes(path); file.writeBytes(path,data.data);")]
    [InlineData("file.transformPatternedBlocks(path,path+'.clear',{algorithm:'blowfish',mode:'cbc',operation:'decrypt',key:'bad'});")]
    [InlineData("var files=file; files.writeBytes(path,'AQIDBA==',{truncate:true});")]
    [InlineData("var rewrite=file['writeBytes']; rewrite(path,'AQIDBA==',{truncate:true});")]
    [InlineData("var decryption={strategy:'decrypt'};")]
    public async Task Playback_PostprocessingUsesTheCompletedArtifact(string processing)
    {
        var (adapter, context, track) = Setup(true, processing);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSuccess);
        var lease = result.RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Equal(processing.Contains("AQIDBA==", StringComparison.Ordinal) ? 4 : prefix.Length + 4,
            (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Theory]
    [InlineData("bytes=999999-", 416)]
    [InlineData("bytes=1-2,4-5", 400)]
    public async Task Playback_RejectsInvalidKnownLengthRanges(string range, int status)
    {
        var (adapter, context, track) = Setup(true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = RangeHeaderValue.Parse(range);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Equal(status, (int)response.StatusCode);
        source.Complete();
    }

    [Fact]
    public async Task Playback_UnknownLengthRangeWaitsForCompletedArtifact()
    {
        var (adapter, context, track) = Setup(false);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = new RangeHeaderValue(10, 20);
        var opening = lease.ProtectedResponseFactory!(request, default);
        Assert.False(opening.IsCompleted);
        source.Complete();
        using var response = await opening.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(11, response.Content.Headers.ContentLength);
        Assert.Equal($"bytes 10-20/{prefix.Length}", response.Content.Headers.ContentRange!.ToString());
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal(prefix[10..21], await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_UnknownLengthSeekStopsOnCancellationOrDeadline(bool cancel)
    {
        var (adapter, context, track) = Setup(false);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = new RangeHeaderValue(10, null);
        var opening = lease.ProtectedResponseFactory!(request, cancellation.Token);
        Assert.False(opening.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await Assert.ThrowsAsync<IOException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        source.Complete();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playback_Mp4RequiresMetadataBeforeMedia(bool metadataFirst)
    {
        var (adapter, context, track) = Setup(true, mimeType: "audio/mp4");
        // Minimal top-level boxes model the placement of container metadata.
        var header = new byte[] { 0, 0, 0, 8, 102, 116, 121, 112, 0, 0, 0, 8, 109, 111, 111, 118,
            0, 0, 0, 0, 109, 100, 97, 116 };
        if (!metadataFirst) (header[12], header[13], header[14], header[15]) = (109, 100, 97, 116);
        header.CopyTo(prefix, 0);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (metadataFirst)
        {
            var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
            Assert.Equal("audio/mp4", lease.Media.MimeType);
            using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
            using var response = await lease.ProtectedResponseFactory!(request, default);
            source.Release([1, 2, 3, 4]);
            source.Complete();
            Assert.Equal(prefix.Length + 4, (await response.Content.ReadAsByteArrayAsync()).Length);
        }
        else
        {
            Assert.False(pending.IsCompleted);
            source.Release([1, 2, 3, 4]);
            source.Complete();
            var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
            using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
            using var response = await lease.ProtectedResponseFactory!(request, default);
        }
    }

    [Fact]
    public async Task Playback_SegmentsPublishOnlyAnOrderedPrefix()
    {
        var (adapter, context, track) = Setup(false, segmented: true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        Assert.True(lease.SupportsSeeking);
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        var received = new byte[prefix.Length];
        await body.ReadExactlyAsync(received);
        Assert.Equal(prefix, received);
        var next = body.ReadAsync(new byte[4]).AsTask();
        Assert.False(next.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        Assert.Equal(4, await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await body.ReadAsync(new byte[1]));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Playback_LastUnpublishedListenerCancellationRetainsTransferUntilIdleGrace()
    {
        var (adapter, context, track) = Setup(true);
        using var cancellation = new CancellationTokenSource();
        context = new(context.Actor, context.ProviderId, context.Account, context.Policy, context.OperationId,
            context.CorrelationId, context.Deadline, cancellation.Token, context.IdempotencyKey);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
        Assert.False(source.Canceled.Task.IsCompleted);
        Assert.Single(Directory.GetDirectories(options.RootPath));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(source.Canceled.Task.IsCompleted);
        Assert.Single(Directory.GetDirectories(options.RootPath));
        clock.Advance(TimeSpan.FromSeconds(1));
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await adapter.DisposeAsync();
        Assert.Empty(Directory.GetDirectories(options.RootPath));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Playback_RejectsTruncationAndByteLimitOverflow(bool truncated, bool capture)
    {
        if (capture)
        {
            "fLaC"u8.CopyTo(prefix);
            prefix[4] = 128;
            prefix[5] = prefix[6] = 0;
            prefix[7] = 34;
        }
        var (adapter, context, track) = Setup(truncated, capture ? "file.delete('unused');" : "",
            maximumBytes: truncated ? 1024 * 1024 : prefix.Length, extension: capture ? ".flac" : "");
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[prefix.Length]);
        var next = body.ReadAsync(new byte[1]).AsTask();
        if (!truncated) source.Release([1, 2, 3, 4]);
        source.Complete();
        await Assert.ThrowsAnyAsync<IOException>(() => next.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Playback_CancelingOneListenerPreservesTheOther()
    {
        var (adapter, context, track) = Setup(true);
        using var cancellation = new CancellationTokenSource();
        var canceledContext = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, context.Policy,
            context.OperationId, context.CorrelationId, context.Deadline, cancellation.Token, context.IdempotencyKey);
        var first = adapter.GetStreamLeaseAsync(canceledContext, new(track));
        var second = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var leases = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, leases[0].RequireValue().ProtectedSourceUri);
        using var firstResponse = await leases[0].RequireValue().ProtectedResponseFactory!(firstRequest, default);
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, leases[1].RequireValue().ProtectedSourceUri);
        using var secondResponse = await leases[1].RequireValue().ProtectedResponseFactory!(secondRequest, default);
        cancellation.Cancel();
        source.Release([1, 2, 3, 4]);
        source.Complete();
        Assert.Equal(prefix.Length + 4, (await secondResponse.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Playback_RecognizesContainerHeadersWithGenericResponseType()
    {
        var (adapter, context, track) = Setup(false, mimeType: "application/octet-stream");
        "fLaC"u8.CopyTo(prefix);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        Assert.Equal("audio/flac", lease.Media.MimeType);
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        source.Complete();
        Assert.Equal(prefix, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Playback_UnknownLengthHeadDoesNotInventAContentLength()
    {
        var (adapter, context, track) = Setup(false);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Head, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Empty(response.Headers.AcceptRanges);
        source.Complete();
    }

    [Fact]
    public async Task Playback_PatternedDecryptionWaitsAndServesTheTransformedFile()
    {
        var (adapter, context, track) = Setup(true, """
            var clear=file.transformPatternedBlocks(path,path+'.clear',{
              algorithm:'blowfish',mode:'cbc',operation:'decrypt',key:'1234567890123456',
              iv:'0001020304050607',ivEncoding:'hex',segmentSize:2048,transformEvery:3});
            if (!clear.success) throw new Error('decryption failed');
            file.delete(path);
            path=clear.path;
            """);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(prefix.Length + 4, bytes.Length);
        Assert.False(prefix.AsSpan(0, 2048).SequenceEqual(bytes.AsSpan(0, 2048)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes[^4..]);
    }

    [Fact]
    public async Task Playback_DifferentOwnersUseSeparateTransfersAndWorkspaces()
    {
        var (adapter, context, track) = Setup(true);
        var otherActor = new ProviderActorContext(ProviderActorKind.User, Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "test", "other"));
        var other = new ProviderExecutionContext(otherActor, context.ProviderId, context.Account, context.Policy,
            context.OperationId, context.CorrelationId, context.Deadline, context.CancellationToken, context.IdempotencyKey);
        var first = adapter.GetStreamLeaseAsync(context, new(track));
        var second = adapter.GetStreamLeaseAsync(other, new(track));
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Directory.GetDirectories(options.RootPath).Length);
        source.Fail();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.False(result.IsSuccess));
        Assert.Equal(2, requests);
        Assert.Empty(Directory.GetDirectories(options.RootPath));
    }

    [Fact]
    public async Task Playback_CancelingAPendingFallbackListenerPreservesTheSharedDownload()
    {
        var (adapter, context, track) = Setup(true, "file.writeBytes(path,'AQIDBA==',{truncate:true});");
        using var cancellation = new CancellationTokenSource();
        var canceledContext = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, context.Policy,
            context.OperationId, context.CorrelationId, context.Deadline, cancellation.Token, context.IdempotencyKey);
        var first = adapter.GetStreamLeaseAsync(canceledContext, new(track));
        var second = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var canceled = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ProviderErrorKind.Canceled, canceled.Error!.Kind);
        Assert.False(second.IsCompleted);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        var lease = (await second.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Playback_DownloadDeadlineContinuesAfterLeasePublication()
    {
        var (adapter, context, track) = Setup(true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[prefix.Length]);
        clock.Advance(TimeSpan.FromSeconds(61));
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<IOException>(() => body.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
    }

    private (ExtensionDownloadStreamingCapabilityAdapter, ProviderExecutionContext, ProviderExternalResourceId) Setup(bool knownLength, string processing = "", string mimeType = "audio/flac", bool segmented = false, long maximumBytes = 1024 * 1024, bool deezer = false, string extension = "",
        allstarr.Core.Music.IProviderAudioVerifier? audioVerifier = null)
    {
        var manifestSource = """
            {"name":"growing-test","displayName":"Growing test","version":"1.0.0","type":["download_provider"],
             "permissions":{"network":["media.example.test"]}}
            """;
        if (deezer) manifestSource = manifestSource.Replace("growing-test", "patterned-" + Path.GetFileName(root));
        var origin = deezer ? "https://media.test.dzcdn.net/" : "https://media.example.test/";
        if (deezer) manifestSource = manifestSource.Replace("media.example.test", "media.test.dzcdn.net");
        var transfer = deezer
            ? "var encrypted=path+'.encrypted.flac'; var result=file.download('https://media.test.dzcdn.net/audio',encrypted,{});"
            : segmented
            ? "path += '.flac'; var result=file.downloadSegments(['https://media.example.test/first','https://media.example.test/second'],path,{maxParallel:2,maxAttempts:1});"
            : "var result=file.download('https://media.example.test/audio',path,{});";
        var script = $$$"""
            registerExtension({download:function(id,quality,path){
              path += '{{{extension}}}';
              {{{transfer}}}
              if (!result.success) throw new Error('download failed');
              {{{processing}}}
              return {success:true,path:path,actual_extension:'.flac'};
            }});
            """;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(manifestSource, script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Handler(request =>
        {
            Interlocked.Increment(ref requests);
            HttpContent content = request.RequestUri!.AbsolutePath == "/first" ? new ByteArrayContent(prefix) : new StreamContent(source);
            content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            if (knownLength) content.Headers.ContentLength = prefix.Length + 4;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        })));
        sandbox = new ExtensionSandbox(root, normalized, script, factory.Object, logger,
            new ExtensionRuntimePermissionSet(new HashSet<string>([origin]), new HashSet<string>(["retry"]), new HashSet<string>()),
            Path.Combine(root, "runtime"), timeProvider: clock);
        options = new() { RootPath = Path.Combine(root, "workspaces"), MaximumArtifactBytes = maximumBytes };
        var resolver = new ProviderDownloadArtifactResolver(new Mock<IProviderDownloadArtifactStore>().Object, options, audioVerifier: audioVerifier ?? FixtureAudioVerifier.Instance);
        var actor = new ProviderActorContext(ProviderActorKind.User, Guid.CreateVersion7(), new ProviderBackendPrincipal("jellyfin", "test", "user"));
        var context = new ProviderExecutionContext(actor, manifest.Id, null,
            new ProviderExecutionPolicy(new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, false, true, [manifest.Id]),
            "playback-test", "playback-test", DateTimeOffset.UtcNow.AddMinutes(1), default, "playback-test");
        var adapter = new ExtensionDownloadStreamingCapabilityAdapter(sandbox, manifest, null, resolver, options);
        adapters.Add(adapter);
        return (adapter, context, new(manifest.Id, ProviderResourceKind.Track, deezer ? "deezer:123" : "track"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var adapter in adapters) await adapter.DisposeAsync();
        source.Complete();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class PlaybackLogger : Microsoft.Extensions.Logging.ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class SteppedStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int offset;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PrefixConsumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(byte[] bytes) => chunks.Writer.TryWrite(bytes);
        public void Complete() => chunks.Writer.TryComplete();
        public void Fail() => chunks.Writer.TryComplete(new IOException("Transfer interrupted."));
        public override int Read(byte[] buffer, int start, int count) => ReadAsync(buffer.AsMemory(start, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (current == null || offset == current.Length)
            {
                Waiting.TrySetResult();
                if (current != null) PrefixConsumed.TrySetResult();
                try { if (!await chunks.Reader.WaitToReadAsync(cancellationToken)) return 0; }
                catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
                current = await chunks.Reader.ReadAsync(cancellationToken);
                offset = 0;
            }
            var length = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, length).CopyTo(buffer);
            offset += length;
            return length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
