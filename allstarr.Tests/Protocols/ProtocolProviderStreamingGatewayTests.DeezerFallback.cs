using System.Collections.Immutable;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public async Task DeezerAccountFailureStreamsFromExtensionOnFirstRequestAndRetry(bool startPreference, bool discoverAlternative, bool playbackIdentity)
    {
        var fixture = new DeezerFallbackFixture(startPreference, discoverAlternative: discoverAlternative, playbackIdentity: playbackIdentity);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stream = await fixture.Open();
            Assert.NotNull(stream);
            using var response = stream.Response;
            Assert.Equal("spotiflac-deezer", stream.ServingProviderId);
            Assert.Equal("3610902262", stream.ServingExternalId);
        }
        Assert.Equal(1, fixture.Secrets.Calls);
        Assert.Equal(discoverAlternative ? 1 : 0, fixture.Lookups);
    }

    [Fact]
    public async Task DeezerAccountFailureWithoutAlternativePreservesAuthorizationFailure()
    {
        var fixture = new DeezerFallbackFixture(startPreference: true, alternative: false);
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open());
        Assert.Equal((403, "External provider account is unavailable"), JellyfinController.MapExternalStreamException(error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeezerAccountFailureImmediateRetryDuringCooldownPreservesForbiddenStatusAndBody(bool startPreference)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new PlaybackHealthClock(now);
        var timeProvider = new ManualTimeProvider(now);
        var health = new ProviderRuntimeHealth(clock: clock);
        var attempts = 0;
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(() =>
            {
                attempts++;
                return Task.FromResult(ProviderOutcome<ProviderStreamLease>.Failure(
                    ProviderError.TrackUnavailable(ProviderErrorKind.Forbidden)));
            });
        var (gateway, _) = SingleGateway(first, outcomes: health, clock: timeProvider, startPreference: startPreference);
        var context = TimedContext(timeProvider, startPreference);

        // First request: tries source and fails with 403 Forbidden
        var firstError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.OpenStreamAsync(context, "deezer", "3658406082", ProviderAudioQuality.Any, null));
        var (firstStatus, firstMessage) = JellyfinController.MapExternalStreamException(firstError);
        Assert.Equal(403, firstStatus);
        Assert.Equal("External provider account is unavailable", firstMessage);
        Assert.Equal(1, attempts);

        // Advance 14s (while 30s cooldown lasts)
        clock.UtcNow = clock.UtcNow.AddSeconds(14);
        timeProvider.Advance(TimeSpan.FromSeconds(14));

        // Retry 14s later: answers the exact same status and body without re-attempting the source
        var retryError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.OpenStreamAsync(context, "deezer", "3658406082", ProviderAudioQuality.Any, null));
        var (retryStatus, retryMessage) = JellyfinController.MapExternalStreamException(retryError);
        Assert.Equal(firstStatus, retryStatus);
        Assert.Equal(firstMessage, retryMessage);
        Assert.Equal(1, attempts);

        // After the back-off expires (31s from start, >30s cooldown)
        clock.UtcNow = clock.UtcNow.AddSeconds(17);
        timeProvider.Advance(TimeSpan.FromSeconds(17));

        // After back-off expires: source is tried again!
        var afterExpiryError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.OpenStreamAsync(context, "deezer", "3658406082", ProviderAudioQuality.Any, null));
        var (expiredStatus, expiredMessage) = JellyfinController.MapExternalStreamException(afterExpiryError);
        Assert.Equal(firstStatus, expiredStatus);
        Assert.Equal(firstMessage, expiredMessage);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SongWithAnotherEligibleSourcePlaysOnRetryWhileFirstSourceIsCoolingDown(bool startPreference)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new PlaybackHealthClock(now);
        var timeProvider = new ManualTimeProvider(now);
        var health = new ProviderRuntimeHealth(clock: clock);
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Failure(
                ProviderError.TrackUnavailable(ProviderErrorKind.Forbidden)));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, outcomes: health, clock: timeProvider);
        var context = TimedContext(timeProvider, startPreference);

        // First attempt: deezer fails, advances to qobuz which plays
        using var response1 = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("qobuz", Assert.Single(response1.Headers.GetValues("X-Allstarr-Provider")));

        // Deezer is now cooling down
        var execution = new ProviderExecutionContext(context.RequireActor(), "deezer", null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, true, ["deezer"]), "playback", "retry", context.Deadline, default);
        var quality = startPreference ? ProviderAudioQuality.HighResolution : ProviderAudioQuality.Any;
        Assert.True(health.IsPlaybackCoolingDown(execution, new("deezer", ProviderResourceKind.Track, "source-track"), quality));

        // Retry 14s later: deezer is cooling down, qobuz still plays on retry
        clock.UtcNow = clock.UtcNow.AddSeconds(14);
        timeProvider.Advance(TimeSpan.FromSeconds(14));

        using var response2 = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("qobuz", Assert.Single(response2.Headers.GetValues("X-Allstarr-Provider")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitedCooldownImmediateRetryPreservesRateLimitedStatusAndBody(bool startPreference)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new PlaybackHealthClock(now);
        var timeProvider = new ManualTimeProvider(now);
        var health = new ProviderRuntimeHealth(clock: clock);
        var attempts = 0;
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(() =>
            {
                attempts++;
                return Task.FromResult(ProviderOutcome<ProviderStreamLease>.Failure(
                    new ProviderError(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30))));
            });
        var (gateway, _) = SingleGateway(first, outcomes: health, clock: timeProvider, startPreference: startPreference);
        var context = TimedContext(timeProvider, startPreference);

        var firstError = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null));
        var (firstStatus, firstMessage) = JellyfinController.MapExternalStreamException(firstError);
        Assert.Equal(503, firstStatus);
        Assert.Equal("External provider is rate limiting requests", firstMessage);
        Assert.Equal(1, attempts);

        // Immediate retry 14s later
        clock.UtcNow = clock.UtcNow.AddSeconds(14);
        timeProvider.Advance(TimeSpan.FromSeconds(14));

        var retryError = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null));
        var (retryStatus, retryMessage) = JellyfinController.MapExternalStreamException(retryError);
        Assert.Equal(firstStatus, retryStatus);
        Assert.Equal(firstMessage, retryMessage);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DeezerAccountFailureInspectionUsesKnownExtensionWithoutDeviceIdentity()
    {
        var fixture = new DeezerFallbackFixture(startPreference: false, playbackIdentity: false);
        var stream = await fixture.Open(inspection: true);
        using var response = Assert.IsType<ProtocolProviderStream>(stream).Response;
        Assert.Equal("spotiflac-deezer", stream.ServingProviderId);
        Assert.Equal(0, fixture.Lookups);
    }

    [Fact]
    public async Task StreamLeaseAuthorizationExceptionUsesAnotherEligibleSource()
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        first.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ThrowsAsync(new UnauthorizedAccessException("The source account is unavailable."));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        using var response = (await gateway.OpenStreamAsync(ClientContext(), "deezer", "source", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
    }

    [Fact]
    public async Task DeezerDegradedSourceFollowsHealthyExtensionAndRemainsEligible()
    {
        var fixture = new DeezerFallbackFixture(startPreference: false, degraded: true);
        using var response = (await fixture.Open())!.Response;
        Assert.Equal("spotiflac-deezer", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        Assert.Equal(0, fixture.Secrets.Calls);
        var plan = await fixture.Router.PlanAsync<IProviderStreamingCapability>(fixture.LastRequest!);
        Assert.Equal(["spotiflac-deezer", "deezer"], plan.Candidates.Select(item => item.Provider.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeezerCooldownStillDiscoversAnUntriedSource(bool startPreference)
    {
        var health = new ProviderRuntimeHealth();
        var fixture = new TrackFallbackFixture(outcomes: health, startPreference: startPreference);
        var context = ClientContext();
        var source = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "source");
        var execution = new ProviderExecutionContext(context.RequireActor(), "deezer", null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, true, ["deezer"]), "playback", "retry", context.Deadline, default);
        var quality = startPreference ? ProviderAudioQuality.HighResolution : ProviderAudioQuality.Any;
        health.ObservePlayback(execution, source, quality,
            new(ProviderErrorKind.TransientFailure), TimeSpan.Zero);
        using var response = (await fixture.Open())!.Response;
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        Assert.Single(fixture.Links);
        Assert.True(health.IsPlaybackCoolingDown(execution, source, quality));
    }

    private sealed class DeezerFallbackFixture
    {
        internal MissingDeezerSecret Secrets { get; } = new();
        internal IProviderRouter Router { get; }
        internal ProviderRouteRequest? LastRequest { get; private set; }
        internal int Lookups { get; private set; }
        private readonly ProtocolProviderGateway gateway;
        private readonly ProtocolExecutionContext context;

        internal DeezerFallbackFixture(bool startPreference, bool alternative = true, bool degraded = false,
            bool discoverAlternative = false, bool playbackIdentity = true)
        {
            var clients = new HttpClientFactory();
            var native = new DeezerStreamingCapabilityAdapter(clients.CreateClient("test"), Secrets,
                new DeezerMediaClient(new DeezerHttpClient(clients.CreateClient("test")), NullLogger<DeezerMediaClient>.Instance), null);
            var extension = Streaming("spotiflac-deezer", (_, _) => Task.FromResult(AudioResponse()));
            var recording = Guid.NewGuid();
            var linked = !discoverAlternative;
            var availability = extension.As<IProviderRecordingStreamingCapability>();
            availability.SetupGet(item => item.SupportsRecordingAvailability).Returns(true);
            availability.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderExecutionContext>(),
                    It.IsAny<ProviderRecordingAvailabilityRequest>(), It.IsAny<ProviderAudioQuality>()))
                .Callback(() => Lookups++)
                .ReturnsAsync(ProviderOutcome<ProviderRecordingAvailability>.Success(new(new("spotiflac-deezer", ProviderResourceKind.Track, "3610902262"))));
            var capabilities = alternative ? new IProviderStreamingCapability[] { native, extension.Object } : [native];
            var registry = new ProviderRegistry(capabilities.Select(implementation => new ProviderRegistration(
                new ProviderDescriptor(implementation.ProviderId, implementation.ProviderId, "Test source", ProviderOrigin.BuiltIn,
                    "1", "1.0", [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                        ProviderAccountRequirement.Required, "1.0", ["getStreamLease"], [ProviderAccountScope.Shared])], new()), [implementation])));
            var accounts = new Mock<IProviderRouteAccountResolver>();
            var snapshots = capabilities.ToDictionary(item => item.ProviderId,
                item => new ProviderAccountContext(Guid.NewGuid(), item.ProviderId, ProviderAccountScope.Shared, 2));
            accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderRouteAccountRequest request, CancellationToken _) => new ProviderRouteAccountResolution(snapshots[request.ProviderId], 2));
            var identities = new Mock<ITrackIdentityService>();
            identities.Setup(item => item.TranslateAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderExternalResourceId>(),
                    It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackIdentityTarget>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderExecutionContext _, ProviderExternalResourceId source, ProviderExecutionContext target,
                    ProviderTrackIdentityTarget _, CancellationToken _) => !linked
                    ? new(TrackIdentityTranslationStatus.TargetNotLinked, recording, null, null)
                    : new TrackIdentityTranslationResult(TrackIdentityTranslationStatus.Translated,
                        recording, null, new(recording, Guid.NewGuid(), new(target.ProviderId, ProviderResourceKind.Track, source.Value),
                            ProviderIdentityScope.Catalog, null, ProviderIdentityVerification.Verified, "availability", 1)));
            identities.Setup(item => item.GetRecordingAvailabilityAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProviderRecordingAvailabilityRequest(recording.ToString("D"), "Song", ["Artist"], null, 180000,
                    new Dictionary<string, string> { ["deezer"] = "3610902262" }));
            identities.Setup(item => item.LinkAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<TrackIdentityLinkRequest>(), It.IsAny<CancellationToken>()))
                .Callback(() => linked = true)
                .ReturnsAsync(new TrackIdentityLinkResult(TrackIdentityLinkStatus.Created, recording, Guid.NewGuid(), null));
            var health = new ProviderRuntimeHealth();
            var routeHealth = new Mock<IProviderRouteHealthSource>();
            routeHealth.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
                .Returns((string provider, Guid? account, ProviderCapabilityKind capability) => degraded && provider == "deezer"
                    ? new(ProviderRouteHealthState.Degraded, false, 2) : health.GetRouteStatus(provider, account, capability));
            var router = new ProviderRouter(registry, accounts.Object, routeHealth.Object, Mock.Of<IProviderRouteSidecarSource>(), identities.Object, health);
            var recordingRouter = new Mock<IProviderRouter>();
            recordingRouter.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
                .Returns((ProviderRouteRequest request) =>
                {
                    LastRequest = request;
                    return router.PlanAsync<IProviderStreamingCapability>(request);
                });
            recordingRouter.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), It.IsAny<int>(), It.IsAny<ProviderError>()))
                .Returns((ProviderRoutePlan<IProviderStreamingCapability> plan, int index, ProviderError error) => router.EvaluateFallback(plan, index, error));
            Router = router;
            context = ClientContext();
            if (!playbackIdentity) context = new(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
                context.Principal, context.CorrelationId, context.Deadline, context.CancellationToken, new("client"));
            var order = capabilities.Select(item => item.ProviderId).ToImmutableArray();
            if (startPreference) context = context with
            {
                Policy = new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Streaming, order),
                    ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                { UserId = context.RequireActor().EffectiveUserId }
            };
            gateway = new(recordingRouter.Object, registry, clients, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Providers:StreamingOrder"] = string.Join(',', order) }).Build(),
                identities: identities.Object, outcomes: health, audioVerifier: FixtureAudioVerifier.Instance);
        }

        internal Task<ProtocolProviderStream?> Open(bool inspection = false) => gateway.OpenStreamAsync(
            inspection ? context with { IsReadOnly = true } : context,
            "deezer", "3610902262", ProviderAudioQuality.Any, null, headOnly: inspection);
    }

    private static ProtocolExecutionContext TimedContext(TimeProvider clock, bool startPreference)
    {
        if (startPreference) return StartPreferenceContext("Balanced", clock);
        var original = ClientContext();
        return new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal, original.CorrelationId, clock.GetUtcNow().AddMinutes(1), original.CancellationToken,
            original.Client);
    }

    private static (ProtocolProviderGateway Gateway, Mock<IProviderRouter> Router) SingleGateway(
        Mock<IProviderStreamingCapability> first,
        IProviderOutcomeObserver? outcomes = null, TimeProvider? clock = null,
        bool startPreference = false)
    {
        var registry = Registry(first.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                new[] { first.Object }
                    .Where(item => request.ProviderPriority.Contains(item.ProviderId))
                    .ToArray()));
        router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
                It.IsAny<int>(), It.IsAny<ProviderError>()))
            .Returns((ProviderRoutePlan<IProviderStreamingCapability> plan, int index, ProviderError error) =>
                new ProviderFallbackDecision<IProviderStreamingCapability>(
                    ProviderFallbackDisposition.StopFailure,
                    error.Code, null));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:StreamingOrder"] = first.Object.ProviderId
        }).Build();
        IEffectiveProviderPolicyResolver? policies = null;
        if (startPreference)
        {
            var resolver = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
            resolver.Setup(item => item.ResolveForUserAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                    new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
                    {
                        [ProviderCapabilityKind.Streaming] = [first.Object.ProviderId]
                    }.ToImmutableDictionary(),
                    ImmutableHashSet<string>.Empty,
                    AudioQualityPolicy.DefaultStep,
                    0.07));
            policies = resolver.Object;
        }
        return (new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), configuration,
            effectivePolicies: policies, outcomes: outcomes, timeProvider: clock, audioVerifier: FixtureAudioVerifier.Instance), router);
    }

    private sealed class MissingDeezerSecret : IProviderAccountSecretAccessor
    {
        internal int Calls { get; private set; }
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            Calls++;
            throw new KeyNotFoundException("The selected provider account has no secret reference.");
        }
    }
}
