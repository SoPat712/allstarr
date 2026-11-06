using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Downloads;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Models.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData("Balanced", false, null)]
    [InlineData("BestQuality", false, null)]
    [InlineData("FastStart", true, null)]
    [InlineData("Balanced", false, "bytes=0-")]
    [InlineData("FastStart", true, "bytes=0-")]
    public async Task PlaybackStartUsesDeclaredQualityAndUpgradesTheLowerQualityCache(string mode, bool cachedStart, string? range)
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        try
        {
            var path = Path.Combine(root, "cache", "cached.mp3");
            await File.WriteAllBytesAsync(path, ManagedTrackCacheServiceTests.ConstantBitrateMp3());
            string? published = null;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "source-track"))
                .ReturnsAsync(path);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "qobuz", "qobuz-track"))
                .ReturnsAsync(() => published);
            local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, song, saved) =>
                {
                    Assert.Equal("qobuz", song.ExternalProvider);
                    published = saved;
                }).Returns(Task.CompletedTask);
            await using var cache = new ManagedTrackCacheService(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build(),
                Options.Create(new SubsonicSettings()), local.Object, NullLogger<ManagedTrackCacheService>.Instance,
                audioVerifier: FixtureAudioVerifier.Instance);
            var first = Streaming("deezer", (_, _) => throw new InvalidOperationException("The cached fallback needs no source request."));
            first.As<IProviderAudioFormatSource>().Setup(item => item.PreferredFormat(It.IsAny<ProviderAudioQuality>()))
                .Returns(new ProviderMediaFormat("audio/flac", "flac", "flac", sampleRate: 44100, bitDepth: 16));
            var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
            second.As<IProviderAudioFormatSource>().Setup(item => item.PreferredFormat(It.IsAny<ProviderAudioQuality>()))
                .Returns(new ProviderMediaFormat("audio/flac", "flac", "flac", sampleRate: 44100, bitDepth: 16));
            using var activity = new PlaybackDeliveryActivityStore();
            var context = StartPreferenceContext(mode);
            var (gateway, _) = FailoverGateway(first, second, activity, managedTrackCache: cache);
            var stream = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, range))!;
            using var response = stream.Response;
            Assert.Equal(cachedStart, stream.IsCached);
            Assert.Equal(cachedStart ? "mp3" : "flac", stream.Lease.Media.Codec);
            if (cachedStart)
            {
                Assert.Equal(128000, stream.Lease.Media.Bitrate);
                Assert.Equal(44100, stream.Lease.Media.SampleRate);
                Assert.Null(stream.Lease.Media.BitDepth);
                var served = activity.StreamFor(context.RequireActor().EffectiveUserId, context.Client.DeviceId, "ext-deezer-song-source-track");
                Assert.Equal(stream.Lease.Media, served!.Media);
            }
            await response.Content.CopyToAsync(Stream.Null);
            await cache.UpgradeCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(published);
            Assert.True(File.Exists(published));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unauthorized)]
    [InlineData(ProviderErrorKind.Forbidden)]
    [InlineData(ProviderErrorKind.NotFound)]
    public async Task PlaybackStartAdvancesImmediatelyAfterAMediaLeaseRefusalAndLogsEachAttempt(ProviderErrorKind failure)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Failure(ProviderError.TrackUnavailable(failure)));
        var second = Streaming("qobuz", (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(25));
            return Task.FromResult(AudioResponse());
        });
        var log = new PlaybackAttemptLog();
        var (gateway, _) = FailoverGateway(first, second, clock: clock, logger: log);
        var started = clock.GetTimestamp();

        using var response = (await gateway.OpenStreamAsync(StartPreferenceContext("Balanced", clock),
            "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.Equal(TimeSpan.FromMilliseconds(25), clock.GetElapsedTime(started));
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        Assert.Equal(2, log.Attempts.Count);
        Assert.Equal(("deezer", ProviderError.TrackUnavailable(failure).Code, 0d), log.Attempts[0]);
        Assert.Equal(("qobuz", "ready", 25d), log.Attempts[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackStartDemotesFailedChecksForLegacyClientsAndCanonicalDownloads(bool download)
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var statuses = PlaybackStatuses();
        RestorePlaybackCheck(statuses, "deezer", download ? "download" : "streaming", false, DateTimeOffset.UtcNow);
        RestorePlaybackCheck(statuses, "qobuz", download ? "download" : "streaming", true, DateTimeOffset.UtcNow);
        var (gateway, _) = FailoverGateway(first, second, providerStatuses: statuses);
        var original = Context();
        var context = download ? DownloadStartContext(ProtocolKind.Jellyfin, "Balanced") :
            new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
                original.Principal, original.CorrelationId, original.Deadline, original.CancellationToken, new("client"));

        using var response = (await gateway.OpenStreamAsync(context, download ? "allstarr" : "deezer",
            "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Theory]
    [InlineData("FastStart", "streaming", true, "qobuz")]
    [InlineData("Balanced", "streaming", true, "qobuz")]
    [InlineData("BestQuality", "streaming", true, "qobuz")]
    [InlineData("Balanced", "metadata", true, "deezer")]
    [InlineData("Balanced", "streaming", false, "deezer")]
    public async Task PlaybackStartDemotesTheLatestFailingCheckOnlyForTheNeededCapability(
        string mode, string failingCapability, bool stillFailing, string expected)
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var context = StartPreferenceContext(mode);
        var healthClock = new PlaybackHealthClock(DateTimeOffset.UtcNow.AddMinutes(-1));
        var health = new ProviderRuntimeHealth(clock: healthClock);
        ObserveStart(health, context, "deezer", TimeSpan.FromMilliseconds(100), 96000, 24);
        ObserveStart(health, context, "qobuz", TimeSpan.FromSeconds(4), 44100, 16);
        healthClock.UtcNow = DateTimeOffset.UtcNow;
        var statuses = PlaybackStatuses(health);
        var at = DateTimeOffset.UtcNow.AddSeconds(-10);
        RestorePlaybackCheck(statuses, "qobuz", "streaming", true, at);
        RestorePlaybackCheck(statuses, "deezer", failingCapability, false, at);
        if (!stillFailing) RestorePlaybackCheck(statuses, "deezer", failingCapability, true, at.AddSeconds(1));
        var (gateway, _) = FailoverGateway(first, second, outcomes: health, providerStatuses: statuses);

        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.Equal(expected, Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        (expected == "deezer" ? second : first).Verify(item => item.GetStreamLeaseAsync(
            It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PlaybackStartKeepsSourcesHealthyThroughIsolatedProbeFailures(int failures)
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var context = StartPreferenceContext("Balanced");
        var health = new ProviderRuntimeHealth();
        ObserveStart(health, context, "deezer", TimeSpan.FromMilliseconds(100), 96000, 24);
        ObserveStart(health, context, "qobuz", TimeSpan.FromSeconds(4), 44100, 16);
        var statuses = PlaybackStatuses(health);
        var at = DateTimeOffset.UtcNow.AddSeconds(-5);
        RestorePlaybackCheck(statuses, "deezer", "streaming", true, at);
        for (var index = 0; index < failures; index++)
            statuses.RestoreCheckResult(ProviderRuntimeStatusKey.CreateAccountFree("deezer", "streaming"), null, new()
            {
                Provider = "deezer", Capability = "streaming", IsSupported = true, IsEnabled = true,
                Configuration = ProviderConfigurationState.Configured, Health = ProviderHealthState.Degraded,
                TestedAt = at.AddSeconds(index + 1), ReasonCode = "probe_failed"
            });
        var (gateway, _) = FailoverGateway(first, second, outcomes: health, providerStatuses: statuses);
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("deezer", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task PlaybackStartKeepsLowBitrateDownloadsBehindBetterAudioEvenInFastStart()
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        second.As<IProviderAudioFormatSource>().Setup(item => item.PreferredFormat(It.IsAny<ProviderAudioQuality>()))
            .Returns(new ProviderMediaFormat("audio/flac", "flac", "flac", sampleRate: 44100, bitDepth: 16));
        var context = DownloadStartContext(ProtocolKind.Jellyfin, "FastStart");
        var health = new ProviderRuntimeHealth();
        var actor = context.RequireActor();
        var execution = new ProviderExecutionContext(actor, "deezer", null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, true, ["deezer"]), "playback", "health-test", context.Deadline, default);
        health.ObservePlaybackStart(execution, ProviderAudioQuality.Any, TimeSpan.FromMilliseconds(100),
            new("audio/mpeg", "mp3", "mp3", 128000));
        ObserveStart(health, context, "qobuz", TimeSpan.FromSeconds(4), 44100, 16);
        var (gateway, _) = FailoverGateway(first, second, outcomes: health);
        using var response = (await gateway.OpenStreamAsync(context, "allstarr", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task PlaybackStartKeepsAFailingSourceAsTheLastResort()
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse(System.Net.HttpStatusCode.NotFound)));
        var statuses = PlaybackStatuses();
        RestorePlaybackCheck(statuses, "deezer", "streaming", false, DateTimeOffset.UtcNow);
        RestorePlaybackCheck(statuses, "qobuz", "streaming", true, DateTimeOffset.UtcNow);
        var (gateway, _) = FailoverGateway(first, second, providerStatuses: statuses);

        using var response = (await gateway.OpenStreamAsync(StartPreferenceContext("Balanced"),
            "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;

        Assert.Equal("deezer", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Fact]
    public void PlaybackStartHealthEvidenceStaysWithinItsAccountAndCredentialRevision()
    {
        var health = new ProviderRuntimeHealth();
        var statuses = PlaybackStatuses(health);
        var account = Guid.CreateVersion7();
        var protocol = StartPreferenceContext("Balanced");
        ProviderExecutionContext Execution(Guid accountId, long revision) => new(protocol.RequireActor(), "deezer",
            new(accountId, "deezer", ProviderAccountScope.Shared, revision),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, true, ["deezer"]),
            "playback", "health-test", protocol.Deadline, default);
        var key = ProviderRuntimeStatusKey.CreateManaged("deezer", "streaming", account);
        statuses.RestoreCheckResult(key, 1, new()
        {
            Provider = "deezer",
            Capability = "streaming",
            IsSupported = true,
            IsEnabled = true,
            Configuration = ProviderConfigurationState.Configured,
            Health = ProviderHealthState.Degraded,
            ReasonCode = "probe_failed",
            TestedAt = DateTimeOffset.UtcNow
        });
        for (var index = 0; index < 3; index++) health.Record(key, 1, false, 200, "probe_failed");

        Assert.Equal(ProviderHealthState.Degraded, statuses.GetLatestCapabilityHealth(Execution(account, 1), ProviderCapabilityKind.Streaming));
        Assert.Equal(ProviderHealthState.Unknown, statuses.GetLatestCapabilityHealth(Execution(account, 2), ProviderCapabilityKind.Streaming));
        Assert.Equal(ProviderHealthState.Unknown, statuses.GetLatestCapabilityHealth(Execution(Guid.CreateVersion7(), 1), ProviderCapabilityKind.Streaming));
        health.Record(key, 1, true, 10, null);
        Assert.Equal(ProviderHealthState.Healthy, statuses.GetLatestCapabilityHealth(Execution(account, 1), ProviderCapabilityKind.Streaming));
    }

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
            var statuses = PlaybackStatuses(health);
            RestorePlaybackCheck(statuses, "qobuz", "streaming", false, DateTimeOffset.UtcNow);
            var (gateway, _) = FailoverGateway(first, second, managedTrackCache: cache, outcomes: health, providerStatuses: statuses);
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
    public async Task PlaybackStartSharedTimeoutStopsTheDownloadAndPendingFallback()
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
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

    private sealed class PlaybackAttemptLog : ILogger<ProtocolProviderGateway>
    {
        public List<(string Provider, string Outcome, double Elapsed)> Attempts { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level != LogLevel.Information || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToDictionary(item => item.Key, item => item.Value);
            if (values.GetValueOrDefault("{OriginalFormat}") as string !=
                "Playback start attempt {Provider}: {Outcome} in {ElapsedMs} ms") return;
            Attempts.Add(((string)values["Provider"]!, (string)values["Outcome"]!, (double)values["ElapsedMs"]!));
        }
    }

    private static ProviderStatusManager PlaybackStatuses(ProviderRuntimeHealth? health = null) => new(
        new ConfigurationBuilder().Build(), new HttpClientFactory(), NullLogger<ProviderStatusManager>.Instance,
        Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
        Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()), health: health);

    private static void RestorePlaybackCheck(ProviderStatusManager statuses, string provider, string capability,
        bool passed, DateTimeOffset at)
    {
        for (var index = passed ? 2 : 0; index < 3; index++)
            statuses.RestoreCheckResult(ProviderRuntimeStatusKey.CreateAccountFree(provider, capability), null, new()
            {
                Provider = provider,
                Capability = capability,
                IsSupported = true,
                IsEnabled = true,
                Configuration = ProviderConfigurationState.Configured,
                Health = passed ? ProviderHealthState.Healthy : ProviderHealthState.Degraded,
                ReasonCode = passed ? null : "probe_failed",
                TestedAt = at.AddMilliseconds(index - 2)
            });
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

    private sealed class PlaybackHealthClock(DateTimeOffset now) : allstarr.Core.Operations.IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
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
