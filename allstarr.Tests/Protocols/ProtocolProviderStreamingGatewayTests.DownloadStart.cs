using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "FastStart", 2, true)]
    [InlineData(ProtocolKind.Jellyfin, "Balanced", 2, false)]
    [InlineData(ProtocolKind.Jellyfin, "BestQuality", 6, true)]
    [InlineData(ProtocolKind.Subsonic, "FastStart", 2, false)]
    [InlineData(ProtocolKind.Subsonic, "Balanced", 2, true)]
    [InlineData(ProtocolKind.Subsonic, "BestQuality", 6, false)]
    public async Task CanonicalDownloadHedgesBeforeHeadersAndPinsEveryFollowingRange(
        ProtocolKind dialect, string mode, int delay, bool deviceIdentity)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var activity = new PlaybackDeliveryActivityStore();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Streaming("deezer", async (_, token) =>
        {
            waiting.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return AudioResponse();
        });
        first.As<IProviderDownloadStreamingCapability>();
        var ranges = new List<string?>();
        var second = Streaming("qobuz", (request, _) =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var response = AudioResponse();
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }[(int)from..]);
            response.Content.Headers.ContentRange = new(from, 3, 4);
            return Task.FromResult(response);
        });
        var (gateway, router) = FailoverGateway(first, second, activity, clock: clock);
        var context = DownloadStartContext(dialect, mode, clock, deviceIdentity);
        var opening = gateway.OpenStreamAsync(context, "allstarr", "recording", ProviderAudioQuality.DataSaver, "bytes=0-");
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(delay));
        Assert.False(opening.IsCompleted);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        clock.Advance(TimeSpan.FromSeconds(delay));
        using var initial = (await opening.WaitAsync(TimeSpan.FromSeconds(5)))!.Response;
        Assert.Equal("qobuz", Assert.Single(initial.Headers.GetValues("X-Allstarr-Provider")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await initial.Content.ReadAsByteArrayAsync());
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var range in new[] { "bytes=2-", "bytes=0-" })
        {
            using var resumed = (await gateway.OpenStreamAsync(context, "allstarr", "recording", ProviderAudioQuality.DataSaver, range))!.Response;
            Assert.Equal("qobuz", Assert.Single(resumed.Headers.GetValues("X-Allstarr-Provider")));
            Assert.Equal(range == "bytes=2-" ? new byte[] { 3, 4 } : new byte[] { 1, 2, 3, 4 }, await resumed.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(new[] { "bytes=0-", "bytes=2-", "bytes=0-" }, ranges);
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.Is<ProviderStreamLeaseRequest>(request => request.RequestedQuality == ProviderAudioQuality.Any && request.PreferOriginalContainer)), Times.Exactly(3));
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.Is<ProviderRouteRequest>(request =>
            request.SourceTrackId != null && request.SourceTrackId.ProviderId == "qobuz" && request.SourceTrackId.Value == "qobuz-track")), Times.Exactly(2));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task CanonicalDownloadRangeFailureCannotSwitchItsSelectedRepresentation(ProtocolKind dialect)
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.BadGateway)));
        var opens = 0;
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse(
            ++opens == 1 ? HttpStatusCode.OK : HttpStatusCode.BadGateway)));
        var (gateway, _) = FailoverGateway(first, second, activity);
        var context = DownloadStartContext(dialect, "Balanced");
        using var initial = (await gateway.OpenStreamAsync(context, "allstarr", "recording", ProviderAudioQuality.Any, null))!.Response;
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(context, "allstarr", "recording", ProviderAudioQuality.Any, "bytes=2-"));
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        Assert.Equal(2, opens);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task ExplicitDownloadKeepsTheExactOriginalAndAllowsASlowValidStart(ProtocolKind dialect)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderExecutionContext? selected = null;
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.As<IProviderDownloadStreamingCapability>();
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((execution, request) =>
            {
                selected = execution;
                Assert.Equal(ProviderAudioQuality.Any, request.RequestedQuality);
                Assert.True(request.PreferOriginalContainer);
            })
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new("lease", new Uri("https://media.example.test/audio"),
                clock.GetUtcNow().AddMinutes(5), true, true, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry,
                async (request, token) =>
                {
                    Assert.Equal("bytes=0-", request.Headers.Range?.ToString());
                    waiting.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return AudioResponse();
                })));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, clock: clock);
        var context = DownloadStartContext(dialect, "FastStart", clock);
        var started = clock.GetUtcNow();
        var opening = gateway.OpenStreamAsync(context, "deezer", "exact-track", ProviderAudioQuality.DataSaver, "bytes=0-");
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(18));
            Assert.False(opening.IsCompleted);
            Assert.Equal(started.AddSeconds(30), selected!.Deadline);
            release.TrySetResult();
            using var response = (await opening.WaitAsync(TimeSpan.FromSeconds(5)))!.Response;
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("deezer", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
            second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    public async Task DownloadStartSharesOneDeadlineAcrossAllResolutionAndQualityWork(ProtocolKind dialect, bool canonical)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = new List<ProviderExecutionContext>();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.As<IProviderDownloadStreamingCapability>();
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(async (ProviderExecutionContext execution, ProviderStreamLeaseRequest _) =>
            {
                executions.Add(execution);
                using var registration = execution.PlaybackAbandonment.Register(() => abandoned.TrySetResult());
                waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, execution.CancellationToken);
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled));
            });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        second.As<IProviderDownloadStreamingCapability>();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(async (ProviderExecutionContext execution, ProviderStreamLeaseRequest _) =>
            {
                executions.Add(execution);
                secondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, execution.CancellationToken);
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled));
            });
        var (gateway, _) = FailoverGateway(first, second, clock: clock);
        var started = clock.GetUtcNow();
        var context = DownloadStartContext(dialect, "Balanced", clock);
        var opening = gateway.OpenStreamAsync(context, canonical ? "allstarr" : "deezer", "track", ProviderAudioQuality.Any, "bytes=0-");
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (canonical)
        {
            await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromSeconds(2));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        clock.Advance(TimeSpan.FromSeconds(canonical ? 13 : 30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(canonical ? 2 : 1, executions.Count);
        Assert.All(executions, execution => Assert.Equal(started.AddSeconds(canonical ? 15 : 30), execution.Deadline));
        Assert.All(executions, execution => Assert.Equal(started.AddSeconds(150), execution.PlaybackTransferDeadline));
        Assert.All(executions, execution => Assert.True(execution.CancellationToken.IsCancellationRequested));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task CanonicalDownloadRejectsBadAudioBeforeSelectingAnOriginal(ProtocolKind dialect)
    {
        var invalid = new ObservedContent("<html>busy</html>"u8.ToArray());
        invalid.Headers.ContentType = new("audio/flac");
        var first = Streaming("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = invalid }));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        using var response = (await gateway.OpenStreamAsync(DownloadStartContext(dialect, "Balanced"),
            "allstarr", "recording", ProviderAudioQuality.Any, "bytes=0-"))!.Response;
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        Assert.True(invalid.Disposed);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task CanonicalDownloadRangeCannotReplaceASelectedPrivateAccount(ProtocolKind dialect)
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var context = DownloadStartContext(dialect, "Balanced");
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        using var selected = AudioResponse();
        activity.StreamOpened(context, "ext-allstarr-song-recording", ProviderAudioQuality.Any,
            new(selected, new("selected", new Uri("https://media.example.test/audio"), context.Deadline,
                true, true, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry),
                "qobuz", "qobuz-track", Guid.NewGuid(), ServingAccountRevision: 7));
        var (gateway, _) = FailoverGateway(first, second, activity);
        await Assert.ThrowsAsync<ProtocolProviderUnavailableException>(() => gateway.OpenStreamAsync(context,
            "allstarr", "recording", ProviderAudioQuality.Any, "bytes=2-"));
        foreach (var source in new[] { first, second })
            source.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    private static ProtocolExecutionContext DownloadStartContext(ProtocolKind dialect, string mode,
        TimeProvider? clock = null, bool deviceIdentity = true)
    {
        var original = StartPreferenceContext(mode, clock, protocol: dialect);
        return new(dialect, original.BackendInstanceId, original.VerifiedBackendPrincipalId, original.Principal,
            original.CorrelationId, original.Deadline, original.CancellationToken,
            deviceIdentity ? new("download-test", "download-device") : new())
        {
            Policy = original.Policy,
            IsOriginalDownload = true,
            RetainOriginalMedia = true
        };
    }
}
