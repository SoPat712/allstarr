using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task OpenStream_ActorlessContextDefersToCompatibilityFallback()
    {
        var gateway = new ProtocolProviderGateway(
            Mock.Of<IProviderRouter>(MockBehavior.Strict),
            new ProviderRegistry([]),
            Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict),
            Mock.Of<IMusicMetadataService>(MockBehavior.Strict),
            new HttpClientFactory());
        var context = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "backend",
            "api-key",
            null,
            "stream-test",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.Null(await gateway.OpenStreamAsync(
            context, "deezer", "track-1", ProviderAudioQuality.Any, null));
    }

    [Fact]
    public async Task PlayableSearch_OnlyQueriesTracksAndIsolatesProviderFailures()
    {
        var failing = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        failing.SetupGet(item => item.ProviderId).Returns("apple-download");
        failing.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        failing.Setup(item => item.SearchTracksAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderMetadataSearchRequest>(request => request.Query == "Track Artist")))
            .ThrowsAsync(new HttpRequestException("unavailable"));
        var healthy = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        healthy.SetupGet(item => item.ProviderId).Returns("deezer");
        healthy.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        healthy.Setup(item => item.SearchTracksAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderMetadataSearchRequest>(request => request.Query == "Track Artist")))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(
                "deezer",
                [
                    new ProviderTrackMetadata(
                        new("deezer", ProviderResourceKind.Track, "track-1"),
                        "Track",
                        [new("Artist")]),
                    new ProviderTrackMetadata(
                        new("musicbrainz", ProviderResourceKind.Track, "metadata-only"),
                        "Metadata only",
                        [new("Artist")])
                ])));
        var registry = MetadataRegistry(failing.Object, healthy.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(
                It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
                EmptyPlan<IProviderStreamingCapability>(request));
        router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(
                It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
                EmptyPlan<IProviderDownloadCapability>(request));
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(
                It.Is<ProviderRouteRequest>(request =>
                    request.Capability == ProviderCapabilityKind.Metadata &&
                    request.ProviderPriority.SequenceEqual(new[] { "apple-download", "deezer" }))))
            .ReturnsAsync((ProviderRouteRequest request) =>
                MetadataPlan(request, registry, failing.Object, healthy.Object));
        var legacy = new Mock<IMusicMetadataService>();
        legacy.Setup(item => item.SearchPlayableSongsAsync(
                "Track Artist", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var gateway = new ProtocolProviderGateway(
            router.Object,
            registry,
            Mock.Of<IProviderRouteAccountResolver>(),
            legacy.Object,
            new HttpClientFactory());

        var songs = await gateway.SearchPlayableSongsAsync(Context(), "Track Artist", 10);

        Assert.Equal("track-1", Assert.Single(songs).ExternalId);
        failing.VerifyAll();
        healthy.VerifyAll();
    }

    [Fact]
    public async Task PlayableSearch_ExcludesProviderWithoutAUsablePlaybackAccount()
    {
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.SetupGet(item => item.ProviderId).Returns("qobuz");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var requiredScopes = new[]
        {
            ProviderAccountScope.Global,
            ProviderAccountScope.User,
            ProviderAccountScope.Library
        };
        var registry = new ProviderRegistry(
        [
            new ProviderRegistration(
                new ProviderDescriptor(
                    "qobuz",
                    "Qobuz",
                    "Test provider",
                    ProviderOrigin.BuiltIn,
                    "1",
                    "1",
                    [
                        new ProviderCapabilityDescriptor(
                            ProviderCapabilityKind.Metadata,
                            ProviderCapabilitySupportState.Supported,
                            ProviderAccountRequirement.None,
                            "1",
                            ["searchTracks", "getTrack"]),
                        new ProviderCapabilityDescriptor(
                            ProviderCapabilityKind.Streaming,
                            ProviderCapabilitySupportState.ConfiguredOnly,
                            ProviderAccountRequirement.Required,
                            "1",
                            allowedAccountScopes: requiredScopes),
                        new ProviderCapabilityDescriptor(
                            ProviderCapabilityKind.Download,
                            ProviderCapabilitySupportState.ConfiguredOnly,
                            ProviderAccountRequirement.Required,
                            "1",
                            allowedAccountScopes: requiredScopes)
                    ],
                    new ProviderPermissionDescriptor()),
                [metadata.Object])
        ]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(
                It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
                EmptyPlan<IProviderStreamingCapability>(request));
        router.Setup(item => item.PlanAsync<IProviderDownloadCapability>(
                It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
                EmptyPlan<IProviderDownloadCapability>(request));
        var accounts = new Mock<IProviderRouteAccountResolver>();
        accounts.Setup(item => item.ResolveAsync(
                It.IsAny<ProviderRouteAccountRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderRouteAccountResolution?)null);
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(
            router.Object,
            registry,
            accounts.Object,
            legacy.Object,
            new HttpClientFactory());

        var songs = await gateway.SearchPlayableSongsAsync(
            Context(), "Track Artist", 10);

        Assert.Empty(songs);
    }

    [Fact]
    public async Task OpenStream_UsesVerifiedRouterFallback()
    {
        var first = Capability("deezer", ProviderOutcome<ProviderStreamLease>.Failure(
            new ProviderError(ProviderErrorKind.TransientFailure)));
        var lease = new ProviderStreamLease(
            "qobuz-lease",
            new Uri("https://media.example.test/qobuz"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            true,
            true,
            new ProviderMediaFormat("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry);
        var second = Capability("qobuz", ProviderOutcome<ProviderStreamLease>.Success(lease));
        var registry = Registry(first.Object, second.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(
                It.Is<ProviderRouteRequest>(request =>
                    request.Policy.AllowFallback &&
                    request.SourceTrackId!.ProviderId == "deezer" &&
                    request.SourceTrackId.Value == "source-track")))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(
                request, registry, first.Object, second.Object));
        router.Setup(item => item.EvaluateFallback(
                It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
                0,
                It.Is<ProviderError>(error => error.Kind == ProviderErrorKind.TransientFailure)))
            .Returns((ProviderRoutePlan<IProviderStreamingCapability> plan, int _, ProviderError _) =>
                new ProviderFallbackDecision<IProviderStreamingCapability>(
                    ProviderFallbackDisposition.Advance, "fallback-transient-failure", plan.Candidates[1]));
        var gateway = new ProtocolProviderGateway(
            router.Object,
            registry,
            Mock.Of<IProviderRouteAccountResolver>(),
            Mock.Of<IMusicMetadataService>(),
            new HttpClientFactory());

        var stream = await gateway.OpenStreamAsync(
            Context(), "deezer", "source-track", ProviderAudioQuality.Lossless, null);

        Assert.NotNull(stream);
        Assert.Equal("qobuz-lease", stream.Lease.LeaseId);
        stream.Response.Dispose();
        first.VerifyAll();
        second.VerifyAll();
    }

    private static Mock<IProviderStreamingCapability> Capability(
        string providerId,
        ProviderOutcome<ProviderStreamLease> outcome)
    {
        var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(providerId);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        capability.Setup(item => item.GetStreamLeaseAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(outcome);
        return capability;
    }

    private static ProviderRegistry Registry(params IProviderStreamingCapability[] capabilities) => new(
        capabilities.Select(capability =>
        {
            var descriptor = new ProviderCapabilityDescriptor(
                ProviderCapabilityKind.Streaming,
                ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None,
                "1.0",
                ["getStreamLease"]);
            return new ProviderRegistration(
                new ProviderDescriptor(
                    capability.ProviderId,
                    capability.ProviderId,
                    "Test provider",
                    ProviderOrigin.BuiltIn,
                    "1",
                    "1.0",
                    [descriptor],
                    new ProviderPermissionDescriptor()),
                [capability]);
        }));

    private static ProviderRegistry MetadataRegistry(params IProviderMetadataCapability[] capabilities) => new(
        capabilities.Select(capability => new ProviderRegistration(
            new ProviderDescriptor(
                capability.ProviderId,
                capability.ProviderId,
                "Test provider",
                ProviderOrigin.BuiltIn,
                "1",
                "1.0",
                [
                    new ProviderCapabilityDescriptor(
                        ProviderCapabilityKind.Metadata,
                        ProviderCapabilitySupportState.Supported,
                        ProviderAccountRequirement.None,
                        "1.0",
                        ["searchTracks", "getTrack"]),
                    new ProviderCapabilityDescriptor(
                        ProviderCapabilityKind.Streaming,
                        ProviderCapabilitySupportState.ConfiguredOnly,
                        ProviderAccountRequirement.None,
                        "1.0")
                ],
                new ProviderPermissionDescriptor()),
            [capability])));

    private static ProviderRoutePlan<IProviderStreamingCapability> Plan(
        ProviderRouteRequest request,
        IProviderRegistry registry,
        params IProviderStreamingCapability[] capabilities)
    {
        var candidates = capabilities.Select((capability, index) =>
        {
            var provider = registry.GetRequired(capability.ProviderId);
            return new ProviderRouteCandidate<IProviderStreamingCapability>(
                index,
                provider,
                provider.Capabilities.Single(),
                capability,
                new ProviderExecutionContext(
                    request.Actor,
                    capability.ProviderId,
                    null,
                    request.Library,
                    request.Policy,
                    request.OperationId,
                    request.CorrelationId,
                    request.Deadline,
                    request.CancellationToken),
                new ProviderExternalResourceId(
                    capability.ProviderId,
                    ProviderResourceKind.Track,
                    $"{capability.ProviderId}-track"));
        }).ToArray();
        return new ProviderRoutePlan<IProviderStreamingCapability>(
            request,
            candidates,
            new ProviderRouteDecisionRecord(
                request.CorrelationId,
                ProviderCapabilityKind.Streaming,
                candidates[0].Provider.Id,
                null,
                candidates.Select(item => new ProviderRouteCandidateDecision(
                    item.Provider.Id, null, ProviderRouteDecisionStatus.Accepted,
                    item.Priority == 0 ? "selected" : "eligible-fallback", item.Priority)).ToArray()));
    }

    private static ProviderRoutePlan<IProviderMetadataCapability> MetadataPlan(
        ProviderRouteRequest request,
        IProviderRegistry registry,
        params IProviderMetadataCapability[] capabilities)
    {
        var candidates = capabilities.Select((capability, index) =>
        {
            var provider = registry.GetRequired(capability.ProviderId);
            return new ProviderRouteCandidate<IProviderMetadataCapability>(
                index,
                provider,
                provider.Capabilities.Single(item =>
                    item.Capability == ProviderCapabilityKind.Metadata),
                capability,
                new ProviderExecutionContext(
                    request.Actor,
                    capability.ProviderId,
                    null,
                    request.Library,
                    request.Policy,
                    request.OperationId,
                    request.CorrelationId,
                    request.Deadline,
                    request.CancellationToken),
                null);
        }).ToArray();
        return new ProviderRoutePlan<IProviderMetadataCapability>(
            request,
            candidates,
            new ProviderRouteDecisionRecord(
                request.CorrelationId,
                ProviderCapabilityKind.Metadata,
                candidates[0].Provider.Id,
                null,
                candidates.Select(item => new ProviderRouteCandidateDecision(
                    item.Provider.Id,
                    null,
                    ProviderRouteDecisionStatus.Accepted,
                    item.Priority == 0 ? "selected" : "eligible-fallback",
                    item.Priority)).ToArray()));
    }

    private static ProviderRoutePlan<TCapability> EmptyPlan<TCapability>(
        ProviderRouteRequest request)
        where TCapability : class, IProviderCapability =>
        new(
            request,
            [],
            new ProviderRouteDecisionRecord(
                request.CorrelationId,
                request.Capability,
                null,
                null,
                []));

    private static ProtocolExecutionContext Context()
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        return new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "backend",
            "principal",
            new AllstarrPrincipal(
                tenant, user, "jellyfin", "backend", "principal", "User", false),
            "stream-test",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None,
            libraryScopeId: "music");
    }

    private sealed class HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
