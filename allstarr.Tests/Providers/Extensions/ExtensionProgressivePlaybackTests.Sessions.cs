using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed partial class ExtensionProgressivePlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_DiscardedCandidateStopsOnlyItsUnsharedDownloadImmediately(bool shared)
    {
        var (adapter, context, track) = SetupConversionPlayback();
        using var discard = new CancellationTokenSource();
        var pending = adapter.GetStreamLeaseAsync(context with { PlaybackAbandonment = discard.Token }, new(track));
        source.Release(prefix);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        var other = shared ? adapter.GetStreamLeaseAsync(context, new(track)) : null;
        discard.Cancel();
        if (other != null)
        {
            Assert.False(source.Canceled.Task.IsCompleted);
            source.Release([1, 2, 3, 4]);
            source.Complete();
            var lease = (await other.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
            using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
            using var response = await lease.ProtectedResponseFactory!(request, default);
            Assert.Equal(ExtensionDownloadConversionTests.Converted, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, requests);
        }
        else
            await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_CanceledUnpublishedCallerReturnsAndReplacementSharesConversion(bool converting)
    {
        var (adapter, context, track) = SetupConversionPlayback();
        var conversionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishConversion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken conversionToken = default;
        var conversions = 0;
        async Task<string> Run(string executable, IEnumerable<string> arguments, string? output, long limit, CancellationToken token)
        {
            if (output != null)
            {
                Interlocked.Increment(ref conversions);
                conversionToken = token;
                conversionStarted.TrySetResult();
                await finishConversion.Task.WaitAsync(token);
            }
            return await ExtensionDownloadConversionTests.FixtureRunner(executable, arguments, output, limit, token);
        }
        sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(30), Run);
        using var cancellation = new CancellationTokenSource();
        var first = adapter.GetStreamLeaseAsync(WithStreamCancellation(context, cancellation.Token), new(track));
        try
        {
            source.Release(prefix);
            await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted);
            if (converting)
            {
                source.Release([1, 2, 3, 4]);
                source.Complete();
                await conversionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            cancellation.Cancel();
            Assert.Equal(ProviderErrorKind.Canceled, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Error!.Kind);
            Assert.False(source.Canceled.Task.IsCompleted);
            Assert.False(conversionToken.IsCancellationRequested);
            Assert.Single(Directory.GetDirectories(options.RootPath));

            var replacement = adapter.GetStreamLeaseAsync(context, new(track));
            Assert.False(replacement.IsCompleted);
            if (!converting)
            {
                source.Release([1, 2, 3, 4]);
                source.Complete();
                await conversionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.False(replacement.IsCompleted);
            Assert.Equal(1, requests);
            finishConversion.TrySetResult();
            var lease = (await replacement.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
            Assert.Equal("flac", lease.Media.Container);
            Assert.Equal("flac", lease.Media.Codec);
            using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
            using var response = await lease.ProtectedResponseFactory!(request, default);
            Assert.Equal(ExtensionDownloadConversionTests.Converted, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, requests);
            Assert.Equal(1, conversions);
        }
        finally
        {
            finishConversion.TrySetResult();
            source.Complete();
        }
    }

    [Fact]
    public async Task Playback_ConvertedArtifactReattachesForTwoRangesAndExpiresAfterLastReaderGrace()
    {
        var (adapter, context, track) = SetupConversionPlayback();
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(prefix);
        source.Release([1, 2, 3, 4]);
        source.Complete();
        var first = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var initial = new HttpRequestMessage(HttpMethod.Get, first.ProtectedSourceUri);
        var response = await first.ProtectedResponseFactory!(initial, default);
        Assert.Equal(ExtensionDownloadConversionTests.Converted, await response.Content.ReadAsByteArrayAsync());
        var artifact = Assert.Single(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
        response.Dispose();
        Assert.True(File.Exists(artifact));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));

        var leases = await Task.WhenAll(adapter.GetStreamLeaseAsync(context, new(track)),
            adapter.GetStreamLeaseAsync(context, new(track))).WaitAsync(TimeSpan.FromSeconds(10));
        using var firstRange = new HttpRequestMessage(HttpMethod.Get, leases[0].RequireValue().ProtectedSourceUri);
        firstRange.Headers.Range = new RangeHeaderValue(0, 3);
        using var secondRange = new HttpRequestMessage(HttpMethod.Get, leases[1].RequireValue().ProtectedSourceUri);
        secondRange.Headers.Range = new RangeHeaderValue(4, 7);
        var firstResponse = await leases[0].RequireValue().ProtectedResponseFactory!(firstRange, default);
        var secondResponse = await leases[1].RequireValue().ProtectedResponseFactory!(secondRange, default);
        Assert.Equal(HttpStatusCode.PartialContent, firstResponse.StatusCode);
        Assert.Equal("bytes 0-3/8", firstResponse.Content.Headers.ContentRange!.ToString());
        Assert.Equal(ExtensionDownloadConversionTests.Converted[..4], await firstResponse.Content.ReadAsByteArrayAsync());
        firstResponse.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(artifact));
        Assert.Equal(HttpStatusCode.PartialContent, secondResponse.StatusCode);
        Assert.Equal("bytes 4-7/8", secondResponse.Content.Headers.ContentRange!.ToString());
        Assert.Equal(ExtensionDownloadConversionTests.Converted[4..], await secondResponse.Content.ReadAsByteArrayAsync());
        secondResponse.Dispose();
        Assert.Equal(1, requests);

        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.True(File.Exists(artifact));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(Directory.GetDirectories(options.RootPath));
        Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Playback_ConversionCompletingDuringIdleGraceDoesNotExtendCleanupDeadline()
    {
        var (adapter, context, track) = SetupConversionPlayback();
        using var cancellation = new CancellationTokenSource();
        var pending = adapter.GetStreamLeaseAsync(WithStreamCancellation(context, cancellation.Token), new(track));
        source.Release(prefix);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var completion = StreamProducerCompletion(adapter);
        cancellation.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Error!.Kind);
        Assert.False(source.Canceled.Task.IsCompleted);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        source.Release([1, 2, 3, 4]);
        source.Complete();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        var artifact = Assert.Single(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
        Assert.Equal(ExtensionDownloadConversionTests.Converted, await File.ReadAllBytesAsync(artifact));
        Assert.Equal(1, requests);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(Directory.GetDirectories(options.RootPath));
        Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Playback_UnpublishedGraceDoesNotExtendProducerDeadline()
    {
        var (adapter, context, track) = SetupConversionPlayback();
        using var cancellation = new CancellationTokenSource();
        var limited = WithStreamCancellation(context, cancellation.Token, clock.GetUtcNow().AddSeconds(5));
        var pending = adapter.GetStreamLeaseAsync(limited, new(track));
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var completion = StreamProducerCompletion(adapter);
        cancellation.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Error!.Kind);
        Assert.False(source.Canceled.Task.IsCompleted);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(source.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, requests);
        Assert.Empty(Directory.GetDirectories(options.RootPath));
        Assert.Empty(Directory.GetFiles(options.RootPath, "*", SearchOption.AllDirectories));
    }

    private (ExtensionDownloadStreamingCapabilityAdapter, ProviderExecutionContext, ProviderExternalResourceId) SetupConversionPlayback()
    {
        var setup = Setup(false, "return {success:true,path:path,actual_extension:'.m4a',audio_codec:'flac',requires_container_conversion:true,duration_ms:1000};",
            "audio/mp4", extension: ".m4a");
        sandbox.DownloadPostProcessor = new(TimeSpan.FromSeconds(30), ExtensionDownloadConversionTests.FixtureRunner);
        return setup;
    }

    private static ProviderExecutionContext WithStreamCancellation(ProviderExecutionContext context, CancellationToken token,
        DateTimeOffset? deadline = null) => new(context.Actor, context.ProviderId, context.Account, context.Policy,
            context.OperationId, context.CorrelationId, deadline ?? context.Deadline, token, context.IdempotencyKey);

    private static Task StreamProducerCompletion(ExtensionDownloadStreamingCapabilityAdapter adapter)
    {
        var type = typeof(ExtensionDownloadStreamingCapabilityAdapter);
        var gate = type.GetField("sessionsGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        lock (gate)
        {
            var sessions = (IDictionary)type.GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
            var session = sessions.Values.Cast<object>().Single();
            return (Task)session.GetType().GetProperty("Completion")!.GetValue(session)!;
        }
    }
}
