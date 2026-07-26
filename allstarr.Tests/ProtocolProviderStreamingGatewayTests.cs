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
