using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task RetainedOriginalGateway_PreservesRetentionAcrossRoutingAndReplaysWithoutAnotherLease()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var opens = 0;
        var gateway = fixture.Gateway((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            Interlocked.Increment(ref opens);
            return Task.FromResult(fixture.Response());
        });
        using (var initial = (await gateway.OpenStreamAsync(fixture.Context, "deezer", "original-track", ProviderAudioQuality.DataSaver, null))!.Response)
            Assert.Equal(fixture.Bytes, await initial.Content.ReadAsByteArrayAsync());
        Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
        using (var replay = (await gateway.OpenStreamAsync(fixture.Context, "deezer", "original-track", ProviderAudioQuality.DataSaver, null))!.Response)
            Assert.Equal(fixture.Bytes, await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, fixture.LeaseCalls);
        Assert.Equal(1, opens);
        Assert.False(fixture.Activity.WasStreamOpened("ext-deezer-song-original-track"));
        Assert.Null(fixture.Activity.StreamFor(fixture.Context.Actor!.EffectiveUserId, "device", "ext-deezer-song-original-track"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 15)]
    public async Task RetainedOriginalGateway_ColdRangeRenewsTheSelectedSourceForOneNonblockingFullFill(int start, int end)
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var fullOpening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullOpens = 0;
        var partialOpens = 0;
        var gateway = fixture.Gateway(async (request, token) =>
        {
            if (request.Headers.Range?.Ranges.Single() is { From: { } from } range)
            {
                Interlocked.Increment(ref partialOpens);
                var to = range.To ?? fixture.Bytes.Length - 1;
                var partial = fixture.Response(fixture.Bytes[(int)from..((int)to + 1)]);
                partial.StatusCode = HttpStatusCode.PartialContent;
                partial.Content.Headers.ContentRange = new(from, to, fixture.Bytes.Length);
                return partial;
            }
            Assert.Null(request.Headers.IfRange);
            Interlocked.Increment(ref fullOpens);
            fullOpening.TrySetResult();
            await fullRelease.Task.WaitAsync(token);
            return fixture.Response();
        });
        try
        {
            using var first = (await gateway.OpenStreamAsync(fixture.Context, "deezer", "original-track", ProviderAudioQuality.Any,
                $"bytes={start}-{end}"))!.Response;
            Assert.Equal(0, fullOpens);
            Assert.Equal(fixture.Bytes[start..(end + 1)], await first.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await fullOpening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var second = (await gateway.OpenStreamAsync(fixture.Context, "deezer", "original-track", ProviderAudioQuality.Any,
                $"bytes={start + 2}-{end + 2}"))!.Response;
            Assert.Equal(fixture.Bytes[(start + 2)..(end + 3)], await second.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, partialOpens);
            Assert.Equal(1, fullOpens);
            Assert.Equal(3, fixture.LeaseCalls);
            Assert.Empty(await fixture.Mappings.ListCacheMappingsAsync());
            first.Dispose();
            second.Dispose();
            fixture.Clock.Advance(TimeSpan.FromSeconds(11));
            fullRelease.TrySetResult();
            await fixture.Cache.TransferCompletionAsync(fixture.Track, fixture.OriginalScope, 7, originalDownload: true).WaitAsync(TimeSpan.FromSeconds(5));
            var mapping = Assert.Single(await fixture.Mappings.ListCacheMappingsAsync());
            Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(mapping.LocalPath));
            Assert.False(fixture.Activity.WasStreamOpened("ext-deezer-song-original-track"));
        }
        finally { fullRelease.TrySetResult(); }
    }

    [Theory]
    [InlineData("head")]
    [InlineData("readonly")]
    [InlineData("unconsumed")]
    public async Task RetainedOriginalGateway_InspectionOrUnusedSelectionNeverStartsAnExtraFullOpen(string use)
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var opens = 0;
        var gateway = fixture.Gateway((request, _) =>
        {
            Interlocked.Increment(ref opens);
            var range = request.Headers.Range?.Ranges.Single();
            if (range == null) return Task.FromResult(fixture.Response());
            var response = fixture.Response(fixture.Bytes[..2]);
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new(0, 1, fixture.Bytes.Length);
            return Task.FromResult(response);
        });
        using var result = (await gateway.OpenStreamAsync(fixture.Context with { IsReadOnly = use == "readonly" },
            "deezer", "original-track", ProviderAudioQuality.Any, "bytes=0-1", headOnly: use == "head"))!.Response;
        if (use == "readonly") Assert.Equal(fixture.Bytes[..2], await result.Content.ReadAsByteArrayAsync());
        result.Dispose();
        Assert.Equal(1, opens);
        Assert.Empty(await fixture.Mappings.ListCacheMappingsAsync());
    }

    [Fact]
    public async Task RetainedOriginalIntent_DoesNotPublishAnUnselectedPlaybackHedge()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Streaming("deezer", async (_, _) =>
        {
            waiting.TrySetResult();
            await release.Task;
            returned.TrySetResult();
            return fixture.Response();
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(fixture.Response()));
        var (gateway, _) = FailoverGateway(first, second, managedTrackCache: fixture.Cache, clock: fixture.Clock);
        var context = StartPreferenceContext("Balanced", fixture.Clock) with { RetainOriginalMedia = true };
        try
        {
            var pending = gateway.OpenStreamAsync(context, "deezer", "original-track", ProviderAudioQuality.Any, null);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            using var selected = (await pending.WaitAsync(TimeSpan.FromSeconds(5)))!.Response;
            Assert.Equal("qobuz", Assert.Single(selected.Headers.GetValues("X-Allstarr-Provider")));
            await selected.Content.CopyToAsync(Stream.Null);
            release.TrySetResult();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(await fixture.Mappings.ListCacheMappingsAsync(), item => item.ProviderId == "deezer");
            first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        }
        finally { release.TrySetResult(); }
    }
}
