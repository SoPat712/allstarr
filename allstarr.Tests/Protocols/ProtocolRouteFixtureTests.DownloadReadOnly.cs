using System.Net;
using System.Collections.Immutable;
using allstarr.Core.Settings;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Subsonic", "/rest/download.view?id=ext-qobuz-song-42&u=fixture&p=fixture&v=1.13.0&c=Feishin", false)]
    [InlineData("Subsonic", "/rest/download.view?id=ext-qobuz-song-42&u=fixture&p=fixture&v=1.13.0&c=Feishin", true)]
    [InlineData("Jellyfin", "/Items/ext-qobuz-song-42/File?api_key=fixture-key&DeviceId=download-device", false)]
    [InlineData("Jellyfin", "/Items/ext-qobuz-song-42/File?api_key=fixture-key&DeviceId=download-device", true)]
    public async Task OfflineDownload_IgnoresPlaybackQualityAndDoesNotWriteIdentity(string backend, string path, bool range)
    {
        byte[] bytes = [102, 76, 97, 67, 0, 0, 0, 34, 1, 2, 3, 4];
        var lease = new ProviderStreamLease("original", new Uri("https://media.example.test/original"),
            DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry, (request, _) =>
            {
                Assert.Equal(range ? "bytes=4-7" : null, request.Headers.Range?.ToString());
                var response = new HttpResponseMessage(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(range ? bytes[4..8] : bytes)
                };
                if (range) response.Content.Headers.ContentRange = new(4, 7, bytes.Length);
                return Task.FromResult(response);
            });
        var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("qobuz");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        capability.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "42" && request.RequestedQuality == ProviderAudioQuality.Any)))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
        var descriptor = new ProviderDescriptor("qobuz", "Qobuz", "Fixture provider", ProviderOrigin.BuiltIn, "1", "1",
            [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getStreamLease"])], new ProviderPermissionDescriptor());
        var registry = new ProviderRegistry([new ProviderRegistration(descriptor, [capability.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderStreamingCapability>(request,
                [new ProviderRouteCandidate<IProviderStreamingCapability>(0, descriptor, descriptor.Capabilities.Single(), capability.Object,
                    new ProviderExecutionContext(request.Actor, "qobuz", null, request.Policy, request.OperationId,
                        request.CorrelationId, request.Deadline, request.CancellationToken), request.SourceTrackId)],
                new ProviderRouteDecisionRecord(request.CorrelationId, ProviderCapabilityKind.Streaming, "qobuz", null,
                    [new ProviderRouteCandidateDecision("qobuz", null, ProviderRouteDecisionStatus.Accepted, "selected", 0)])));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        using var activity = new allstarr.Services.Common.PlaybackDeliveryActivityStore();
        using var factory = new ProtocolFactory(backend, request =>
            request.RequestUri!.AbsolutePath is "/Users/Me" or "/rest/ping.view"
                ? DownloadAuthentication(backend)
                : throw new InvalidOperationException($"Unexpected native request: {request.RequestUri.AbsolutePath}"),
            services =>
            {
                services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
                var policies = new Mock<IEffectiveProviderPolicyResolver>();
                policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                        ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                        ImmutableHashSet<string>.Empty, "DataSaver", 0.07)
                    { UserId = user });
                services.RemoveAll<IEffectiveProviderPolicyResolver>();
                services.AddSingleton(policies.Object);
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton<IProtocolProviderGateway>(new ProtocolProviderGateway(router.Object, registry,
                    new StubHttpClientFactory(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected request"))),
                    identities: identities.Object, playbackActivity: activity, effectivePolicies: policies.Object));
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (range) request.Headers.Range = new(4, 7);
        using var response = await client.SendAsync(request);
        Assert.Equal(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(range ? bytes[4..8] : bytes, await response.Content.ReadAsByteArrayAsync());
        capability.VerifyAll();
        router.VerifyAll();
        identities.VerifyNoOtherCalls();
        Assert.Null(activity.StreamFor(Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd"),
            "download-device", "ext-qobuz-song-42"));
    }
}
