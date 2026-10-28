using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Models.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PlaybackStartUpgradesAReplayToAnAlreadyCachedHigherQualityCopyWithoutFetchingEitherSource(ProtocolKind protocol)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "cached.flac");
            var bytes = new byte[42];
            "fLaC"u8.CopyTo(bytes);
            bytes[4] = 0x80;
            bytes[7] = 34;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8),
                (96000UL << 44) | (1UL << 41) | (23UL << 36) | 17280000UL);
            await File.WriteAllBytesAsync(path, bytes);
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "source-track"))
                .ReturnsAsync((string?)null);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "qobuz", "qobuz-track"))
                .ReturnsAsync(path);
            await using var cache = new ManagedTrackCacheService(new ConfigurationBuilder().Build(),
                Options.Create(new SubsonicSettings()), local.Object, NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: FixtureAudioVerifier.Instance);
            var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
            var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
            var context = StartPreferenceContext("Balanced", protocol: protocol);
            var health = new ProviderRuntimeHealth();
            ObserveStart(health, context, "deezer", TimeSpan.FromMilliseconds(400), 44100, 16);
            ObserveStart(health, context, "qobuz", TimeSpan.FromSeconds(12), 96000, 24);
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, outcomes: health);
            var opened = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!;
            using var response = opened.Response;
            Assert.True(opened.IsCached);
            Assert.Equal("qobuz", opened.ServingProviderId);
            Assert.Equal(24, opened.Lease.Media.BitDepth);
            Assert.Equal(96000, opened.Lease.Media.SampleRate);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            foreach (var source in new[] { first, second })
                source.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                    It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("FastStart", 7, "qobuz")]
    [InlineData("Balanced", 7, "qobuz")]
    [InlineData("Balanced", 3, "deezer")]
    [InlineData("BestQuality", 7, "deezer")]
    public async Task PlaybackStartUsesMeasuredQualityAndRecentStartTimeWithoutOpeningTheOtherSource(
        string mode, int highSeconds, string expected)
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var context = StartPreferenceContext(mode);
        var health = new ProviderRuntimeHealth();
        ObserveStart(health, context, "deezer", TimeSpan.FromSeconds(highSeconds), 96000, 24);
        ObserveStart(health, context, "qobuz", TimeSpan.FromMilliseconds(400), 44100, 16);
        var (gateway, _) = FailoverGateway(first, second, outcomes: health);
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal(expected, Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        (expected == "deezer" ? second : first).Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Theory]
    [InlineData("Balanced", 2)]
    [InlineData("BestQuality", 6)]
    public async Task PlaybackStartHedgesOnlyAfterItsDelayAndCancelsAnUnpublishedSource(string mode, int seconds)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Streaming("deezer", async (_, token) =>
        {
            waiting.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException error) when (token.IsCancellationRequested)
            {
                Assert.Equal(token, error.CancellationToken);
                canceled.TrySetResult();
                throw;
            }
            return AudioResponse();
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, clock: clock);
        var context = StartPreferenceContext(mode, clock);
        var opening = gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(seconds));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        Assert.False(canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        using var response = (await opening.WaitAsync(TimeSpan.FromSeconds(5)))!.Response;
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Fact]
    public async Task PlaybackStartTimeoutStopsTheDownloadAndTreatsItsCanceledResultAsAFallbackFailure()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.As<IProviderDownloadStreamingCapability>();
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderStreamLeaseRequest _) =>
            {
                using var registration = context.PlaybackAbandonment.Register(() => abandoned.TrySetResult());
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
                catch (OperationCanceledException) { }
                return ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.Canceled));
            });
        var second = Streaming("qobuz", async (_, token) =>
        {
            secondStarted.TrySetResult();
            await finishSecond.Task.WaitAsync(token);
            return AudioResponse();
        });
        var (gateway, _) = FailoverGateway(first, second, clock: clock);
        var opening = gateway.OpenStreamAsync(StartPreferenceContext("BestQuality", clock),
            "deezer", "source-track", ProviderAudioQuality.Any, null);
        try
        {
            await clock.WaitForTimersAsync(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15));
            clock.Advance(TimeSpan.FromSeconds(6));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(9));
            await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finishSecond.TrySetResult();
            using var response = (await opening.WaitAsync(TimeSpan.FromSeconds(5)))!.Response;
            Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        }
        finally { finishSecond.TrySetResult(); }
    }

    [Theory]
    [InlineData(ProviderUseMode.ManualOnly)]
    [InlineData(ProviderUseMode.Off)]
    public async Task PlaybackStartNeverSelectsANonAutomaticSourceEvenWhenItsMeasuredStartIsFaster(ProviderUseMode mode)
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var context = StartPreferenceContext("FastStart", nonAutomatic: mode);
        var health = new ProviderRuntimeHealth();
        ObserveStart(health, context, "deezer", TimeSpan.FromSeconds(3), 44100, 16);
        ObserveStart(health, context, "qobuz", TimeSpan.FromMilliseconds(10), 96000, 24);
        var (gateway, _) = FailoverGateway(first, second, outcomes: health);
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("deezer", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    private static ProtocolExecutionContext StartPreferenceContext(string mode, TimeProvider? clock = null,
        ProviderUseMode? nonAutomatic = null, ProtocolKind protocol = ProtocolKind.Jellyfin)
    {
        var original = Context();
        return new(protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal! with { BackendType = protocol.ToString().ToLowerInvariant() }, original.CorrelationId,
            (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(1),
            default, original.Client)
        {
            Policy = new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(
                    ProviderCapabilityKind.Streaming, ["deezer", "qobuz"]), ImmutableHashSet<string>.Empty,
                AudioQualityPolicy.DefaultStep, 0.07)
            {
                UserId = original.RequireActor().EffectiveUserId,
                Preferences = new(PlaybackStart: mode),
                ProviderModes = nonAutomatic is { } value
                    ? ImmutableDictionary<string, ProviderUseMode>.Empty.Add("qobuz", value)
                    : ImmutableDictionary<string, ProviderUseMode>.Empty
            }
        };
    }

    private static void ObserveStart(ProviderRuntimeHealth health, ProtocolExecutionContext protocol, string provider,
        TimeSpan elapsed, int sampleRate, int bitDepth)
    {
        var execution = new ProviderExecutionContext(protocol.RequireActor(), provider, null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, true, [provider]), "playback", "start-test", protocol.Deadline, default);
        health.ObservePlayback(execution, new(provider, ProviderResourceKind.Track, "measured-track"),
            ProviderAudioQuality.HighResolution, null, elapsed,
            new("audio/flac", "flac", "flac", sampleRate: sampleRate, bitDepth: bitDepth));
    }
}
