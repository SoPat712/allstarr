using System.Collections.Immutable;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Health;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(false, "qobuz")]
    [InlineData(true, "deezer")]
    public async Task StreamingProviderProjection_UsesPlaybackStartFactsWithoutOpeningMedia(bool cooling, string expected)
    {
        var first = Streaming("deezer", (_, _) => throw new InvalidOperationException("Unexpected media request"));
        var second = Streaming("qobuz", (_, _) => throw new InvalidOperationException("Unexpected media request"));
        var observer = new Mock<IProviderOutcomeObserver>();
        observer.Setup(item => item.GetPlaybackStart(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAudioQuality>()))
            .Returns((ProviderExecutionContext context, ProviderAudioQuality _) => context.ProviderId == "qobuz"
                ? new PlaybackStartObservation(TimeSpan.FromMilliseconds(100), new("audio/flac", "flac", "flac")) : null);
        observer.Setup(item => item.IsPlaybackCoolingDown(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderExternalResourceId>(), It.IsAny<ProviderAudioQuality>()))
            .Returns((ProviderExecutionContext context, ProviderExternalResourceId _, ProviderAudioQuality _) => cooling && context.ProviderId == "qobuz");
        var (gateway, _) = FailoverGateway(first, second, outcomes: observer.Object);
        Assert.Equal(expected, await gateway.GetStreamingProviderAsync(ClientContext(), "allstarr", Guid.NewGuid().ToString("D")));
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrackFallback_AcceptedMatchIsStoredAndStreamsWithoutSearchingAgain(bool isrc)
    {
        var fixture = new TrackFallbackFixture(isrc: isrc);
        var opened = await fixture.Open();
        Assert.NotNull(opened);
        using var response = opened.Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal("matched", opened.ServingExternalId);
        var link = Assert.Single(fixture.Links);
        Assert.Equal(fixture.RecordingId, link.CanonicalRecordingId);
        Assert.Equal("matched", link.ExternalId.Value);
        Assert.Equal(ProviderIdentityVerification.Verified, link.Verification);
        var searches = fixture.Searches;
        using var replay = (await fixture.Open())!.Response;
        Assert.Equal(searches, fixture.Searches);
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("explicit")]
    [InlineData("disabled")]
    [InlineData("manual")]
    public async Task TrackFallback_NonqualifyingOrRestrictedMatchPreservesOriginalRefusal(string restriction)
    {
        var fixture = new TrackFallbackFixture(restriction: restriction);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open());
        Assert.Empty(fixture.Links);
        if (restriction is "disabled" or "manual") Assert.Equal(0, fixture.Searches);
    }

    [Fact]
    public async Task TrackFallback_UnresponsiveSearchIsBoundedAndPreservesOriginalRefusal()
    {
        var fixture = new TrackFallbackFixture(stalled: true);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Open());
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Links);
    }

    [Fact]
    public async Task TrackFallback_RefusalCooldownAvoidsReopeningTheTrack()
    {
        var health = new ProviderRuntimeHealth();
        var first = Capability("deezer", ProviderOutcome<ProviderStreamLease>.Failure(ProviderError.TrackUnavailable(ProviderErrorKind.Forbidden)));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.BadGateway)));
        var (gateway, _) = FailoverGateway(first, second, outcomes: health);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(ClientContext(), "deezer", "source", ProviderAudioQuality.Any, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(ClientContext(), "deezer", "source", ProviderAudioQuality.Any, null));
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Fact]
    public async Task TrackFallback_UsesListenerQualityCeiling()
    {
        var fixture = new TrackFallbackFixture(quality: "DataSaver");
        using var opened = (await fixture.Open())!.Response;
        Assert.Equal(ProviderAudioQuality.DataSaver, fixture.OpenedQuality);
    }

    private sealed class TrackFallbackFixture
    {
        internal Guid RecordingId { get; } = Guid.NewGuid();
        internal List<TrackIdentityLinkRequest> Links { get; } = [];
        internal int Searches { get; private set; }
        internal ProviderAudioQuality? OpenedQuality { get; private set; }
        private readonly ProtocolProviderGateway gateway;

        internal TrackFallbackFixture(bool isrc = true, string? restriction = null, string? quality = null, bool stalled = false,
            IProviderOutcomeObserver? outcomes = null, bool startPreference = true)
        {
            var first = Capability("deezer", ProviderOutcome<ProviderStreamLease>.Failure(
                ProviderError.TrackUnavailable(ProviderErrorKind.Forbidden)));
            var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
            second.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
                .Callback<ProviderExecutionContext, ProviderStreamLeaseRequest>((_, request) => OpenedQuality = request.RequestedQuality)
                .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new("lease", new("https://media.example.test/audio"),
                    DateTimeOffset.UtcNow.AddMinutes(5), true, true, new("audio/flac", "flac", "flac"),
                    ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(AudioResponse()))));
            var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
            metadata.SetupGet(item => item.ProviderId).Returns("qobuz");
            metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
            var matched = new ProviderTrackMetadata(new("qobuz", ProviderResourceKind.Track, "matched"),
                restriction == "mismatch" ? "Different song" : "Song", [new ProviderArtistCredit(restriction == "mismatch" ? "Other" : "Artist")],
                duration: TimeSpan.FromSeconds(210), isrc: restriction == "mismatch" || !isrc ? null : "USAAA2400001", isExplicit: restriction == "explicit");
            metadata.Setup(item => item.LookupByIsrcAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderIsrcLookupRequest>()))
                .Callback(() => Searches++).Returns<ProviderExecutionContext, ProviderIsrcLookupRequest>(async (context, _) =>
                {
                    if (stalled) await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                    return ProviderOutcome<ProviderTrackMetadata>.Success(matched);
                });
            metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .Callback(() => Searches++).ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("qobuz", [matched])));
            var descriptors = MetadataRegistry(metadata.Object);
            var sourceRegistry = Registry(first.Object);
            var registry = new ProviderRegistry([
                new(sourceRegistry.GetRequired("deezer"), [first.Object]),
                new(descriptors.GetRequired("qobuz"), [metadata.Object, second.Object])]);
            var router = new Mock<IProviderRouter>(MockBehavior.Strict);
            router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) =>
                {
                    var plan = request.SourceTrackId == null ? Plan(request, registry, second.Object)
                        : Links.Count > 0 ? Plan(request, registry, second.Object, first.Object) : Plan(request, registry, first.Object);
                    return plan with
                    {
                        Candidates = plan.Candidates.Select(candidate => candidate.Provider.Id == "qobuz"
                            ? candidate with { TrackId = new("qobuz", ProviderResourceKind.Track, "matched") } : candidate).ToArray()
                    };
                });
            router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata.Object));
            router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), It.IsAny<int>(), It.IsAny<ProviderError>()))
                .Returns((ProviderRoutePlan<IProviderStreamingCapability> plan, int index, ProviderError error) => new ProviderFallbackDecision<IProviderStreamingCapability>(
                    index + 1 < plan.Candidates.Count ? ProviderFallbackDisposition.Advance : ProviderFallbackDisposition.Exhausted, error.Code, null));
            var identities = new Mock<ITrackIdentityService>();
            identities.Setup(item => item.GetRecordingAvailabilityAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProviderRecordingAvailabilityRequest(RecordingId.ToString("D"), "Song", ["Artist"], isrc ? "USAAA2400001" : null, 210000,
                    new Dictionary<string, string> { ["deezer"] = "source" })
                { ExplicitContentLyrics = 0 });
            identities.Setup(item => item.LinkAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<TrackIdentityLinkRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ProviderExecutionContext, TrackIdentityLinkRequest, CancellationToken>((_, request, _) => Links.Add(request))
                .ReturnsAsync(new TrackIdentityLinkResult(TrackIdentityLinkStatus.Created, RecordingId, Guid.NewGuid(), null));
            identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new Song { ExternalProvider = "deezer", ExternalId = "source", ExplicitContentLyrics = 0 },
                    new Song { ExternalProvider = "qobuz", ExternalId = "matched", ExplicitContentLyrics = restriction == "explicit" ? 1 : 0 }]);
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Streaming, ["deezer", "qobuz"]),
                    restriction == "disabled" ? ImmutableHashSet.Create("qobuz") : ImmutableHashSet<string>.Empty,
                    quality ?? AudioQualityPolicy.DefaultStep, 0.07)
                {
                    ProviderModes = restriction == "manual" ? ImmutableDictionary<string, ProviderUseMode>.Empty.Add("qobuz", ProviderUseMode.ManualOnly) : ImmutableDictionary<string, ProviderUseMode>.Empty,
                    Preferences = new() { ExplicitFilter = restriction == "explicit" ? "CleanOnly" : "All" }
                });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Providers:StreamingOrder"] = "deezer,qobuz", ["Cache:SearchBudgetMilliseconds"] = stalled ? "100" : "3500" }).Build();
            gateway = new(router.Object, registry, new HttpClientFactory(), configuration, identities: identities.Object,
                effectivePolicies: startPreference ? policies.Object : null, outcomes: outcomes, audioVerifier: FixtureAudioVerifier.Instance);
        }

        internal Task<ProtocolProviderStream?> Open() => gateway.OpenStreamAsync(ClientContext(), "deezer", "source", ProviderAudioQuality.Any, null);
    }
}
