using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using HealthState = allstarr.Services.Common.ProviderHealthState;

namespace allstarr.Tests;

public sealed class ProviderRuntimeHealthTests
{
    [Fact]
    public void LyricsConfigurationChangesResetOnlyTheirOwnFailureEvidence()
    {
        var health = Store();
        var observer = (ILyricsOutcomeObserver)health;
        var context = Context();
        var oldConfiguration = LyricsConfigurationId.From("old", 1);
        var currentConfiguration = LyricsConfigurationId.From("current", 2);
        observer.ObserveLyrics(context, new(ProviderErrorKind.TransientFailure), TimeSpan.Zero, oldConfiguration);
        observer.ObserveLyrics(context, new(ProviderErrorKind.TransientFailure), TimeSpan.Zero, oldConfiguration);
        observer.ObserveLyrics(context, new(ProviderErrorKind.TransientFailure), TimeSpan.Zero, currentConfiguration);
        var current = health.GetLyrics(Key(capability: ProviderCapabilities.Lyrics), context.Account!.Revision, currentConfiguration);
        Assert.NotNull(current);
        Assert.Equal(1, current.ConsecutiveFailures);
        Assert.Null(current.RetryAfter);
        Assert.False(health.IsCircuitOpen(current));
        observer.ObserveLyrics(context, null, TimeSpan.Zero, currentConfiguration);
        var success = health.GetLyrics(Key(capability: ProviderCapabilities.Lyrics), context.Account.Revision, currentConfiguration);
        observer.ObserveLyrics(context, new(ProviderErrorKind.TransientFailure), TimeSpan.Zero, oldConfiguration);
        Assert.Same(success, health.GetLyrics(Key(capability: ProviderCapabilities.Lyrics), context.Account.Revision, currentConfiguration));
    }

    [Fact]
    public void PlaybackStartMedianUsesRecentSuccessfulSamplesForTheExactAccountRevisionAndQuality()
    {
        var store = Store();
        var context = Context();
        var track = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "sample");
        var media = new ProviderMediaFormat("audio/flac", "flac", "flac", sampleRate: 44100, bitDepth: 16);
        foreach (var seconds in new[] { 1, 9, 2 })
            store.ObservePlayback(context, track, ProviderAudioQuality.Lossless, null, TimeSpan.FromSeconds(seconds), media);
        var measured = store.GetPlaybackStart(context, ProviderAudioQuality.Lossless)!;
        Assert.Equal(TimeSpan.FromSeconds(2), measured.MedianStart);
        Assert.Equal(media, measured.Media);
        store.ObservePlayback(context, track, ProviderAudioQuality.Lossless,
            new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(30));
        Assert.Equal(measured, store.GetPlaybackStart(context, ProviderAudioQuality.Lossless));
        Assert.Null(store.GetPlaybackStart(context, ProviderAudioQuality.HighResolution));
        Assert.Null(store.GetPlaybackStart(Context(revision: 5), ProviderAudioQuality.Lossless));
        _clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.Null(store.GetPlaybackStart(context, ProviderAudioQuality.Lossless));
    }

    [Fact]
    public void LocalCapacityDoesNotCreateHealthFailuresOrPlaybackCooldowns()
    {
        var store = Store();
        var context = Context();
        var track = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "fixture");
        for (var index = 0; index < 5; index++)
        {
            store.Observe(context, ProviderCapabilityKind.Streaming, ProviderError.LocalCapacity(), TimeSpan.FromMilliseconds(1));
            store.ObservePlayback(context, track, ProviderAudioQuality.Any, ProviderError.LocalCapacity(), TimeSpan.FromMilliseconds(1));
            store.Record(Key(), context.Account?.Revision, false, 1, LocalCapacityException.Code);
        }
        Assert.False(store.IsPlaybackCoolingDown(context, track, ProviderAudioQuality.Any));
        Assert.Null(store.Get(Key(), context.Account?.Revision));
        Assert.True(ProviderRuntimeStatus.IsNeutralReason(LocalCapacityException.Code));
    }

    [Fact]
    public void AppleDownloadMissingSessionIsReportedWithoutSuppressingCatalog()
    {
        var store = Store();
        store.AppleDownloadSnapshot = new(allstarr.Services.AppleMusic.AppleDownloadEndpointState.NeedsAuthentication,
            "endpoint_authentication_required", "2.0.0", false, []);
        foreach (var capability in new[] { ProviderCapabilityKind.Streaming, ProviderCapabilityKind.Download,
                     ProviderCapabilityKind.Lyrics })
        {
            var status = store.GetRouteStatus("apple-download", _account, capability);
            Assert.Equal(ProviderRouteHealthState.Unavailable, status.State);
            Assert.Equal("apple-download-sign-in-required", status.ReasonCode);
        }
        Assert.Equal(ProviderRouteHealthState.Unknown,
            store.GetRouteStatus("apple-musickit", _account, ProviderCapabilityKind.Metadata).State);
        store.AppleDownloadSnapshot = new(allstarr.Services.AppleMusic.AppleDownloadEndpointState.Available,
            null, "2.0.0", true, []);
        Assert.Null(store.GetRouteStatus("apple-musickit", _account, ProviderCapabilityKind.Streaming).ReasonCode);
        store.AppleDownloadSnapshot = new(allstarr.Services.AppleMusic.AppleDownloadEndpointState.Incompatible,
            "unsupported_api_version", "1.0.0", false, []);
        Assert.Equal("apple-download-gateway-update-required",
            store.GetRouteStatus("apple-musickit", _account, ProviderCapabilityKind.Streaming).ReasonCode);
    }

    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);
    private readonly Guid _account = Guid.CreateVersion7();
    private ProviderRuntimeStatusKey Key(string provider = "deezer", string capability = "streaming", Guid? account = null) =>
        ProviderRuntimeStatusKey.CreateManaged(provider, capability, account ?? _account);
    private ProviderRuntimeHealth Store() => new(clock: _clock);

    [Fact]
    public void ObservationsSeparateProviderAccountCapabilityAndRevision()
    {
        var store = Store();
        store.Record(Key(), 2, true, 12, null);
        store.Record(Key(capability: "playlist"), 2, false, 30, "unauthorized");
        store.Record(Key(account: Guid.NewGuid()), 2, false, 30, "timeout");
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 2)!.Health);
        Assert.Equal(HealthState.Degraded, store.Get(Key(capability: "playlist"), 2)!.Health);
        Assert.Null(store.Get(Key(), 3));
        Assert.Null(store.Get(Key(provider: "qobuz"), 2));
        Assert.Null(store.Get(ProviderRuntimeStatusKey.CreateAccountFree("deezer", "streaming")));
    }

    [Fact]
    public async Task ConcurrentFailuresAreAtomicAndOldRevisionCannotOverwriteNewCredentials()
    {
        var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.Record(Key(), 1, false, 1, "timeout"))));
        var failed = store.Get(Key(), 1)!;
        Assert.Equal(3, failed.ConsecutiveFailures);
        Assert.True(store.IsCircuitOpen(failed));
        store.Record(Key(), 2, true, 7, null);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.Record(Key(), 1, false, 1, "timeout"))));
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 2)!.Health);
        Assert.Null(store.Get(Key(), 1));
    }

    [Fact]
    public void CooldownExpiresAndSuccessfulRetryClosesCircuit()
    {
        var store = Store();
        for (var i = 0; i < 3; i++) store.Record(Key(), 1, false, 3, "timeout");
        Assert.True(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
        _clock.UtcNow += TimeSpan.FromSeconds(61);
        var route = store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming);
        Assert.False(route.CircuitOpen);
        Assert.Equal(ProviderRouteHealthState.Unknown, route.State);
        Assert.Equal(HealthState.Degraded, store.Get(Key(), 1)!.Health);
        store.Record(Key(), 1, true, 10, null);
        Assert.Equal(ProviderRouteHealthState.Healthy, store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).State);
        Assert.Equal(0, store.Get(Key(), 1)!.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(ProviderErrorKind.NotFound)]
    [InlineData(ProviderErrorKind.NotSupported)]
    [InlineData(ProviderErrorKind.IncompatibleMedia)]
    [InlineData(ProviderErrorKind.Forbidden)]
    [InlineData(ProviderErrorKind.Canceled)]
    public void ResourceAndPolicyFailuresAreNotOutages(ProviderErrorKind kind)
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, new(kind), TimeSpan.Zero);
        Assert.Null(store.Get(Key()));
    }

    [Fact]
    public void PassiveFailuresPreserveHealthUntilThresholdAndSuccessResetsFailures()
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, null, TimeSpan.FromMilliseconds(7));
        for (var failure = 1; failure <= 3; failure++)
        {
            _clock.UtcNow += TimeSpan.FromSeconds(1);
            store.Observe(Context(), ProviderCapabilityKind.Streaming,
                new(ProviderErrorKind.TransientFailure), TimeSpan.FromMilliseconds(12));
            var observation = store.Get(Key(), 4)!;
            Assert.Equal(failure < 3 ? HealthState.Healthy : HealthState.Degraded, observation.Health);
            Assert.Equal(failure, observation.ConsecutiveFailures);
            Assert.Equal("transient-failure", observation.ReasonCode);
            Assert.Equal(_clock.UtcNow, observation.ObservedAt);
            Assert.Equal(failure == 3, store.IsCircuitOpen(observation));
        }

        store.Observe(Context(), ProviderCapabilityKind.Streaming, null, TimeSpan.Zero);
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 4)!.Health);
        Assert.Equal(0, store.Get(Key(), 4)!.ConsecutiveFailures);
        Assert.Null(store.Get(Key(), 4)!.ReasonCode);
        Assert.Null(store.Get(Key(), 4)!.RetryAfter);
        store.Observe(Context(), ProviderCapabilityKind.Streaming,
            new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 4)!.Health);
        Assert.Equal(1, store.Get(Key(), 4)!.ConsecutiveFailures);
    }

    [Fact]
    public void PassiveFailureWithoutPreviousEvidenceRemainsUnknown()
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming,
            new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        Assert.Equal(HealthState.Unknown, store.Get(Key(), 4)!.Health);
    }

    [Fact]
    public void ExpiredFailuresDoNotCountTowardThePassiveThreshold()
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, null, TimeSpan.Zero);
        for (var failure = 0; failure < 2; failure++)
            store.Observe(Context(), ProviderCapabilityKind.Streaming,
                new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        _clock.UtcNow += TimeSpan.FromMinutes(6);
        store.Observe(Context(), ProviderCapabilityKind.Streaming,
            new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        Assert.Equal(1, store.Get(Key(), 4)!.ConsecutiveFailures);
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 4)!.Health);
        Assert.False(store.IsCircuitOpen(store.Get(Key(), 4)!));
    }

    [Fact]
    public void PassiveFailuresUseConfiguredThresholdAndDoNotBorrowAnotherRevisionHealth()
    {
        var store = new ProviderRuntimeHealth(new() { FailureThreshold = 2 }, _clock);
        store.Record(Key(), 3, true, 1, null);
        store.Observe(Context(), ProviderCapabilityKind.Streaming,
            new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        Assert.Equal(HealthState.Unknown, store.Get(Key(), 4)!.Health);
        store.Observe(Context(), ProviderCapabilityKind.Streaming,
            new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        Assert.Equal(HealthState.Degraded, store.Get(Key(), 4)!.Health);
        Assert.True(store.IsCircuitOpen(store.Get(Key(), 4)!));
    }

    [Fact]
    public void DisplayRetainsExpiredEvidenceOnlyForTheMatchingRevision()
    {
        var store = Store();
        store.Record(Key(), 4, true, 7, null);
        var observation = store.Get(Key(), 4);
        _clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.Null(store.Get(Key(), 4));
        Assert.Same(observation, store.GetForDisplay(Key(), 4));
        Assert.Null(store.GetForDisplay(Key(), 5));
    }

    [Fact]
    public void DisplayRetentionEvictsOldestKeysAtCapacity()
    {
        var store = Store();
        store.Record(Key(), 4, true, 7, null);
        _clock.UtcNow += TimeSpan.FromSeconds(1);
        var lastKey = Key();
        for (var index = 0; index < 4096; index++)
        {
            lastKey = Key(account: Guid.NewGuid());
            store.Record(lastKey, 4, true, 7, null);
        }
        Assert.Null(store.GetForDisplay(Key(), 4));
        Assert.NotNull(store.GetForDisplay(lastKey, 4));
    }

    [Fact]
    public void ExplicitFailureDegradesImmediately()
    {
        var store = Store();
        store.Record(Key(), 4, true, 1, null);
        store.Record(Key(), 4, false, 1, "timeout");
        Assert.Equal(HealthState.Degraded, store.Get(Key(), 4)!.Health);
        Assert.Equal(1, store.Get(Key(), 4)!.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(ProviderErrorKind.Unauthorized)]
    [InlineData(ProviderErrorKind.AccountNeedsReauthentication)]
    [InlineData(ProviderErrorKind.AccountNeedsConfiguration)]
    public void CredentialsFailureImmediatelyOpensExactRevisionCircuit(ProviderErrorKind kind)
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, null, TimeSpan.Zero);
        store.Observe(Context(), ProviderCapabilityKind.Streaming, new(kind), TimeSpan.FromMilliseconds(2));
        Assert.Equal(HealthState.Degraded, store.Get(Key(), 4)!.Health);
        var route = store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming);
        Assert.True(route.CircuitOpen);
        Assert.Equal(4, route.AccountRevision);
    }

    [Fact]
    public void RateLimitHonorsRetryAfterAndRestartHasNoHydratedState()
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, null, TimeSpan.Zero);
        store.Observe(Context(), ProviderCapabilityKind.Streaming, new(ProviderErrorKind.RateLimited, TimeSpan.FromMinutes(10)), TimeSpan.Zero);
        Assert.Equal(HealthState.Degraded, store.Get(Key(), 4)!.Health);
        _clock.UtcNow += TimeSpan.FromMinutes(9);
        Assert.True(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
        Assert.Null(Store().Get(Key()));
        _clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.False(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
    }

    [Fact]
    public void RetryAfterKeepsRouteOpenPastTheSampleTtl()
    {
        var store = Store();
        store.Observe(Context(), ProviderCapabilityKind.Streaming, null, TimeSpan.Zero);
        store.Observe(Context(), ProviderCapabilityKind.Streaming,
            new(ProviderErrorKind.RateLimited, TimeSpan.FromMinutes(10)), TimeSpan.Zero);

        _clock.UtcNow += TimeSpan.FromMinutes(6);

        Assert.NotNull(store.Get(Key(), 4));
        Assert.True(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
        _clock.UtcNow += TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(1));
        Assert.False(store.GetRouteStatus("deezer", _account, ProviderCapabilityKind.Streaming).CircuitOpen);
    }

    [Fact]
    public void CanceledProbeCannotRestoreOverAConcurrentObservation()
    {
        var store = Store();
        store.Record(Key(), 4, false, 1, "timeout");
        var probe = store.BeginProbe(Key(), 4);
        Assert.Equal(HealthState.Testing, store.Get(Key(), 4)!.Health);
        store.Record(Key(), 5, true, 1, null);
        store.CancelProbe(probe);
        Assert.Equal(HealthState.Healthy, store.Get(Key(), 5)!.Health);
        Assert.Null(store.BeginProbe(Key(), 4));
    }

    [Fact]
    public void PassiveFailureDuringCanceledProbeDoesNotLeaveDisplayTesting()
    {
        var store = Store();
        store.Record(Key(), 4, true, 7, null);
        var probe = store.BeginProbe(Key(), 4);
        store.Observe(Context(), ProviderCapabilityKind.Streaming,
            new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        store.CancelProbe(probe);
        _clock.UtcNow += TimeSpan.FromMinutes(6);
        var observation = store.GetForDisplay(Key(), 4)!;
        Assert.Equal(HealthState.Healthy, observation.Health);
        Assert.Equal("transient-failure", observation.ReasonCode);
        Assert.Equal(1, observation.ConsecutiveFailures);
    }

    [Fact]
    public void CanceledProbeRestoresPreviousObservationAndExpiredStateIsUnknown()
    {
        var store = Store();
        store.Record(Key(), 4, true, 7, null);
        var original = store.Get(Key());
        var probe = store.BeginProbe(Key(), 4);
        store.CancelProbe(probe);
        Assert.Same(original, store.Get(Key()));
        _clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.Null(store.Get(Key()));
    }

    [Fact]
    public void RawDiagnosticTextNeverEntersStatus()
    {
        var store = Store();
        store.Record(Key(), 1, false, 1, "https://private.invalid?token=secret");
        Assert.Equal("probe_failed", store.Get(Key())!.ReasonCode);
    }

    [Fact]
    public void PlaybackCooldownSeparatesItemAccountRevisionAndQualityAndExpires()
    {
        var store = Store();
        var context = Context();
        var track = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "track");
        store.ObservePlayback(context, track, ProviderAudioQuality.Lossless,
            new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(47)), TimeSpan.Zero);
        Assert.True(store.IsPlaybackCoolingDown(context, track, ProviderAudioQuality.Lossless));
        Assert.False(store.IsPlaybackCoolingDown(context, new("deezer", ProviderResourceKind.Track, "other"), ProviderAudioQuality.Lossless));
        Assert.False(store.IsPlaybackCoolingDown(context, track, ProviderAudioQuality.HighResolution));
        var revised = new ProviderExecutionContext(context.Actor, context.ProviderId,
            new(_account, "deezer", ProviderAccountScope.Shared, 5), context.Policy,
            context.OperationId, context.CorrelationId, context.Deadline, default);
        Assert.False(store.IsPlaybackCoolingDown(revised, track, ProviderAudioQuality.Lossless));
        _clock.UtcNow += TimeSpan.FromSeconds(48);
        Assert.False(store.IsPlaybackCoolingDown(context, track, ProviderAudioQuality.Lossless));
        store.ObservePlayback(context, track, ProviderAudioQuality.Lossless, new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        store.ObservePlayback(context, track, ProviderAudioQuality.Lossless, null, TimeSpan.Zero);
        Assert.False(store.IsPlaybackCoolingDown(context, track, ProviderAudioQuality.Lossless));
    }

    private ProviderExecutionContext Context(long revision = 4) => new(
        new ProviderActorContext(ProviderActorKind.Administrator, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            new ProviderBackendPrincipal("jellyfin", "fixture", "admin")), "deezer",
        new ProviderAccountContext(_account, "deezer", ProviderAccountScope.Shared, revision),
        new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, true, true, true, ["deezer"]),
        "test", "health-test", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = now; }
}
