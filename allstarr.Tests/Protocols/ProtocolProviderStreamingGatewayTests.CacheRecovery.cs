using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
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
    public async Task CleanOnlyDoesNotRecoverUnknownCachedAudio(ProtocolKind protocol)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(protocol, "healthy", cleanOnly: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Gateway.OpenStreamAsync(fixture.Context,
            "allstarr", fixture.Recording, ProviderAudioQuality.Any, "bytes=0-"));
        Assert.Empty(fixture.Router.Requests);
        fixture.AssertNoProviderOpens();
        fixture.Library.Verify(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "circuit")]
    [InlineData(ProtocolKind.Subsonic, "circuit")]
    [InlineData(ProtocolKind.Jellyfin, "degraded")]
    [InlineData(ProtocolKind.Subsonic, "degraded")]
    [InlineData(ProtocolKind.Jellyfin, "unavailable")]
    [InlineData(ProtocolKind.Subsonic, "unavailable")]
    [InlineData(ProtocolKind.Jellyfin, "unauthorized")]
    [InlineData(ProtocolKind.Subsonic, "unauthorized")]
    [InlineData(ProtocolKind.Jellyfin, "sidecar")]
    [InlineData(ProtocolKind.Subsonic, "sidecar")]
    public async Task CompleteCacheReplaySurvivesOperationalProviderFailures(ProtocolKind dialect, string failure)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(dialect, failure);
        using var response = (await fixture.Gateway.OpenStreamAsync(fixture.Context, "allstarr", fixture.Recording,
            ProviderAudioQuality.Any, "bytes=44-"))!.Response;
        Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Contains(fixture.Router.Requests, request => request.CacheOnly);
        fixture.AssertNoProviderOpens();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task ReplayChecksAnUnhealthyAlternativeCacheBeforeOpeningAHealthySource(ProtocolKind dialect, bool timeOffset)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(dialect, "unavailable", alternate: true);
        var context = timeOffset ? fixture.Context with { AudioDelivery = new() { StartTimeTicks = TimeSpan.TicksPerSecond } } : fixture.Context;
        var opened = await fixture.Gateway.OpenStreamAsync(context, "allstarr", fixture.Recording,
            ProviderAudioQuality.Any, timeOffset ? null : "bytes=10-19");
        using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
        Assert.True(opened.IsCached);
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
        fixture.AssertNoProviderOpens();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task HealthyCompleteCacheReplayDoesNotRequestASecondPlan(ProtocolKind dialect)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(dialect, "healthy");
        using var response = (await fixture.Gateway.OpenStreamAsync(fixture.Context, "allstarr", fixture.Recording,
            ProviderAudioQuality.Any, "bytes=0-"))!.Response;
        Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.False(Assert.Single(fixture.Router.Requests).CacheOnly);
        fixture.AssertNoProviderOpens();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task AnExplicitManualSourceMayRecoverItsOwnCompleteCache(ProtocolKind dialect)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(dialect, "unavailable", denial: "manual");
        var opened = await fixture.Gateway.OpenStreamAsync(fixture.Context, "deezer", "deezer-track",
            ProviderAudioQuality.Any, "bytes=44-");
        using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
        Assert.Equal("deezer", opened.ServingProviderId);
        Assert.Equal(fixture.Bytes, await response.Content.ReadAsByteArrayAsync());
        fixture.AssertNoProviderOpens();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task CacheRecoveryMissNeverUsesItsCandidatesForRemotePlayback(ProtocolKind dialect)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(dialect, "unavailable", cached: false);
        Assert.Null(await fixture.Gateway.OpenStreamAsync(fixture.Context, "allstarr", fixture.Recording,
            ProviderAudioQuality.Any, "bytes=44-"));
        Assert.Contains(fixture.Router.Requests, request => request.CacheOnly);
        fixture.AssertNoProviderOpens();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "head")]
    [InlineData(ProtocolKind.Subsonic, "head")]
    [InlineData(ProtocolKind.Jellyfin, "inspection")]
    [InlineData(ProtocolKind.Subsonic, "inspection")]
    [InlineData(ProtocolKind.Jellyfin, "download")]
    [InlineData(ProtocolKind.Subsonic, "download")]
    [InlineData(ProtocolKind.Jellyfin, "preparation")]
    [InlineData(ProtocolKind.Subsonic, "preparation")]
    [InlineData(ProtocolKind.Jellyfin, "fresh")]
    [InlineData(ProtocolKind.Subsonic, "fresh")]
    public async Task CacheOnlyRecoveryIsRestrictedToActualReplay(ProtocolKind dialect, string operation)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(dialect, "unavailable");
        var context = fixture.Context with
        {
            IsReadOnly = operation == "inspection",
            IsOriginalDownload = operation == "download",
            IsPreparation = operation == "preparation"
        };
        Assert.Null(await fixture.Gateway.OpenStreamAsync(context, "allstarr", fixture.Recording,
            ProviderAudioQuality.Any, operation == "fresh" ? null : "bytes=44-", headOnly: operation == "head"));
        Assert.DoesNotContain(fixture.Router.Requests, request => request.CacheOnly);
        fixture.AssertNoProviderOpens();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "ownership", "account-personal-to-another-listener")]
    [InlineData(ProtocolKind.Subsonic, "ownership", "account-personal-to-another-listener")]
    [InlineData(ProtocolKind.Jellyfin, "revision", "account-stale")]
    [InlineData(ProtocolKind.Subsonic, "revision", "account-stale")]
    [InlineData(ProtocolKind.Jellyfin, "off", "provider-disabled")]
    [InlineData(ProtocolKind.Subsonic, "off", "provider-disabled")]
    [InlineData(ProtocolKind.Jellyfin, "manual", "manual-selection-required")]
    [InlineData(ProtocolKind.Subsonic, "manual", "manual-selection-required")]
    [InlineData(ProtocolKind.Jellyfin, "terms", "provider-terms-denied")]
    [InlineData(ProtocolKind.Subsonic, "terms", "provider-terms-denied")]
    [InlineData(ProtocolKind.Jellyfin, "identity", "verified-identity-required")]
    [InlineData(ProtocolKind.Subsonic, "identity", "verified-identity-required")]
    public async Task CacheOnlyRoutingPreservesAuthorizationAndRecordingPolicy(ProtocolKind dialect, string denial, string reason)
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(dialect, "unavailable", denial: denial);
        var request = fixture.Request(cacheOnly: true, termsAllowed: denial != "terms");
        var plan = await fixture.Router.PlanAsync<IProviderStreamingCapability>(request);
        Assert.Empty(plan.Candidates);
        Assert.Equal(reason, Assert.Single(plan.Decision.Candidates).ReasonCode);
        fixture.Library.Verify(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        fixture.AssertNoProviderOpens();
    }

    [Fact]
    public async Task CacheOnlyStreamingSkipsAnUnusedRateBudgetButPreservesTerms()
    {
        await using var fixture = await CacheRecoveryFixture.CreateAsync(ProtocolKind.Jellyfin, "unavailable");
        var request = fixture.Request(cacheOnly: true, rateBudget: false);
        Assert.Single((await fixture.Router.PlanAsync<IProviderStreamingCapability>(request)).Candidates);
        var normal = await fixture.Router.PlanAsync<IProviderStreamingCapability>(request with { CacheOnly = false });
        Assert.Empty(normal.Candidates);
        Assert.Equal("rate-limit-budget-exhausted", Assert.Single(normal.Decision.Candidates).ReasonCode);
        fixture.AssertNoProviderOpens();
    }

    [Theory]
    [InlineData(false, "rate-limit-budget-exhausted")]
    [InlineData(true, "health-unavailable")]
    public async Task CacheOnlyDoesNotRelaxMetadataRouting(bool rateBudget, string reason)
    {
        var metadata = Mock.Of<IProviderMetadataCapability>(item => item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Metadata);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get("deezer", null, ProviderCapabilityKind.Metadata))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unavailable, false));
        var router = new ProviderRouter(MetadataRegistry(metadata), Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), Mock.Of<ITrackIdentityService>());
        var context = ClientContext();
        var request = new ProviderRouteRequest(ProviderCapabilityKind.Metadata, context.RequireActor(),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, true, ["deezer"]), "metadata", context.CorrelationId, context.Deadline, ["deezer"],
            [new("deezer", rateLimitBudgetAvailable: rateBudget)])
        { CacheOnly = true };
        var plan = await router.PlanAsync<IProviderMetadataCapability>(request);
        Assert.Empty(plan.Candidates);
        Assert.Equal(reason, Assert.Single(plan.Decision.Candidates).ReasonCode);
    }

    private sealed class CacheRecoveryRouter(IProviderRouter inner) : IProviderRouter
    {
        public List<ProviderRouteRequest> Requests { get; } = [];
        public Task<ProviderRoutePlan<TCapability>> PlanAsync<TCapability>(ProviderRouteRequest request)
            where TCapability : class, IProviderCapability
        {
            Requests.Add(request);
            return inner.PlanAsync<TCapability>(request);
        }
        public ProviderFallbackDecision<TCapability> EvaluateFallback<TCapability>(ProviderRoutePlan<TCapability> plan,
            int failedCandidateIndex, ProviderError error) where TCapability : class, IProviderCapability =>
            inner.EvaluateFallback(plan, failedCandidateIndex, error);
    }

    private sealed class CacheRecoveryFixture : IAsyncDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("allstarr-cache-recovery-").FullName;
        private readonly List<Mock<IProviderStreamingCapability>> providers = [];
        private ManagedTrackCacheService cache = null!;
        public string Recording { get; } = Guid.NewGuid().ToString("D");
        public byte[] Bytes { get; } = PlaybackAudioFixtures.Wave();
        public ProtocolExecutionContext Context { get; private set; } = null!;
        public CacheRecoveryRouter Router { get; private set; } = null!;
        public ProtocolProviderGateway Gateway { get; private set; } = null!;
        public Mock<ILocalLibraryService> Library { get; } = new(MockBehavior.Strict);

        public static async Task<CacheRecoveryFixture> CreateAsync(ProtocolKind dialect, string failure,
            bool alternate = false, bool cached = true, string? denial = null, bool cleanOnly = false)
        {
            var fixture = new CacheRecoveryFixture { Context = PipelineContext(dialect) };
            var path = Path.Combine(fixture.root, "original.wav");
            await File.WriteAllBytesAsync(path, fixture.Bytes);
            fixture.Library.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(),
                    It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((DownloadedSongMappingScope _, string provider, string _) =>
                    cached && provider == (alternate ? "qobuz" : "deezer") ? path : null);
            var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
            identities.Setup(item => item.ResolveTargetAsync(It.IsAny<ProviderExecutionContext>(), Guid.Parse(fixture.Recording),
                    It.IsAny<ProviderTrackIdentityTarget>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderExecutionContext _, Guid recording, ProviderTrackIdentityTarget target, CancellationToken _) =>
                    new TrackIdentityResolution(recording, Guid.NewGuid(), new(target.ProviderId, ProviderResourceKind.Track, target.ProviderId + "-track"),
                        ProviderIdentityScope.Catalog, null, denial == "identity" ? ProviderIdentityVerification.Unknown : ProviderIdentityVerification.Verified,
                        "fixture", 1));
            var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
            var account = new ProviderAccountContext(Guid.NewGuid(), "deezer", ProviderAccountScope.Personal, 1,
                ownerUserId: fixture.Context.RequireActor().EffectiveUserId);
            if (denial == "ownership")
                accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ProviderAccountAudienceDeniedException());
            else
                accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ProviderRouteAccountResolution(account, denial == "revision" ? 2 : 1));
            var modes = ImmutableDictionary<string, ProviderUseMode>.Empty;
            if (denial is "off" or "manual") modes = modes.Add("deezer", denial == "off" ? ProviderUseMode.Off : ProviderUseMode.ManualOnly);
            var policy = new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "High", 0.07)
            { ProviderModes = modes, Preferences = new(cleanOnly ? "CleanOnly" : "All") };
            var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(policy);
            var registrations = new List<ProviderRegistration>();
            foreach (var provider in alternate ? new[] { "deezer", "qobuz" } : new[] { "deezer" })
            {
                var implementation = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
                implementation.SetupGet(item => item.ProviderId).Returns(provider);
                implementation.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
                fixture.providers.Add(implementation);
                var capability = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                    denial is "ownership" or "revision" ? ProviderAccountRequirement.Required : ProviderAccountRequirement.None,
                    "1", ["getStreamLease"], allowedAccountScopes: denial is "ownership" or "revision" ? [ProviderAccountScope.Personal] : [],
                    sidecarDependency: failure == "sidecar" ? "fixture-runtime" : null);
                registrations.Add(new(new(provider, provider, "Fixture", ProviderOrigin.BuiltIn, "1", "1", [capability], new()), [implementation.Object]));
            }
            var registry = new ProviderRegistry(registrations);
            var health = new Mock<IProviderRouteHealthSource>(MockBehavior.Strict);
            health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), ProviderCapabilityKind.Streaming))
                .Returns((string provider, Guid? _, ProviderCapabilityKind _) =>
                    new ProviderRouteHealthSnapshot(alternate && provider == "deezer" ? ProviderRouteHealthState.Healthy : failure switch
                    {
                        "degraded" => ProviderRouteHealthState.Degraded,
                        "unavailable" => ProviderRouteHealthState.Unavailable,
                        "unauthorized" => ProviderRouteHealthState.Unauthorized,
                        _ => ProviderRouteHealthState.Healthy
                    }, failure == "circuit"));
            var router = new ProviderRouter(registry, accounts.Object, health.Object, Mock.Of<IProviderRouteSidecarSource>(),
                identities.Object, effectivePolicies: policies.Object);
            fixture.Router = new(router);
            var configuration = new ConfigurationBuilder().Build();
            fixture.cache = new(configuration, Options.Create(new SubsonicSettings()), fixture.Library.Object,
                NullLogger<ManagedTrackCacheService>.Instance, audioVerifier: FixtureAudioVerifier.Instance);
            fixture.Gateway = new(fixture.Router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict), configuration,
                managedTrackCache: fixture.cache, effectivePolicies: policies.Object, audioVerifier: FixtureAudioVerifier.Instance);
            return fixture;
        }

        public ProviderRouteRequest Request(bool cacheOnly, bool termsAllowed = true, bool rateBudget = true) => new(
            ProviderCapabilityKind.Streaming, Context.RequireActor(),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, true, ["deezer"]), "cache-recovery", Context.CorrelationId, Context.Deadline, ["deezer"],
            [new("deezer", availableQualities: [ProviderAudioQuality.Lossless], providerTermsAllowed: termsAllowed, rateLimitBudgetAvailable: rateBudget)],
            new("allstarr", ProviderResourceKind.Track, Recording))
        { CacheOnly = cacheOnly };

        public void AssertNoProviderOpens()
        {
            foreach (var provider in providers)
                provider.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }

        public async ValueTask DisposeAsync()
        {
            await cache.DisposeAsync();
            Directory.Delete(root, true);
        }
    }
}
