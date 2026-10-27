using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionProgressivePlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaylistPlayback_SeekKeepsPublishedSourceOrRecoversWithAFreshSource(bool changeEncoding)
    {
        var (adapter, context, _) = Setup(true);
        var track = new ProviderExternalResourceId(adapter.ProviderId, ProviderResourceKind.Track, "tidal:123");
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "test", "user",
            new AllstarrPrincipal(context.Actor.EffectiveUserId!.Value, "jellyfin", "test", "user", "Listener", false),
            "playlist-seek", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None,
            new ProtocolClientDescriptor("client", "device"))
        { RequestedItemId = $"ext-allstarr-song-{Guid.NewGuid():D}" };
        var descriptor = new ProviderDescriptor(adapter.ProviderId, adapter.ProviderId, "Fixture", ProviderOrigin.Extension,
            "1", "1.0", [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1.0", ["getStreamLease"])], new ProviderPermissionDescriptor(), entryPoint: "index.js");
        var other = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        other.SetupGet(item => item.ProviderId).Returns("qobuz");
        other.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        other.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new("replacement", new Uri("https://media.example.test/replacement"),
                DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry, (request, _) =>
                {
                    Assert.Equal($"bytes={prefix.Length}-", request.Headers.Range!.ToString());
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([5, 6, 7, 8]) };
                    response.Content.Headers.ContentRange = new(prefix.Length, prefix.Length + 3, prefix.Length + 4);
                    return Task.FromResult(response);
                })));

        var otherDescriptor = new ProviderDescriptor("qobuz", "qobuz", "Fixture", ProviderOrigin.BuiltIn,
            "1", "1.0", descriptor.Capabilities, new ProviderPermissionDescriptor());
        var registry = new ProviderRegistry([new ProviderRegistration(descriptor, [adapter]),
            new ProviderRegistration(otherDescriptor, [other.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var plans = 0;
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var resuming = ++plans > 1;
                var selected = resuming && changeEncoding
                    ? new ProviderExternalResourceId("qobuz", ProviderResourceKind.Track, "other-track")
                    : request.SourceTrackId?.ProviderId == adapter.ProviderId ? request.SourceTrackId : resuming
                        ? new ProviderExternalResourceId(adapter.ProviderId, ProviderResourceKind.Track, "123") : track;
                var provider = resuming && changeEncoding ? otherDescriptor : descriptor;
                var execution = new ProviderExecutionContext(request.Actor, provider.Id, null, request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                var candidate = new ProviderRouteCandidate<IProviderStreamingCapability>(0, provider,
                    provider.Capabilities.Single(), resuming && changeEncoding ? other.Object : adapter, execution, selected);
                return new ProviderRoutePlan<IProviderStreamingCapability>(request, [candidate],
                    new(request.CorrelationId, ProviderCapabilityKind.Streaming, adapter.ProviderId, null, []));
            });
        using var activity = new PlaybackDeliveryActivityStore();
        var gateway = new ProtocolProviderGateway(router.Object, registry,
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict), playbackActivity: activity);
        var opening = gateway.OpenStreamAsync(protocol, "allstarr", protocol.RequestedItemId[17..], ProviderAudioQuality.Any, null);
        source.Release(prefix);
        using var first = (await opening.WaitAsync(TimeSpan.FromSeconds(10)))!.Response;
        Assert.Equal(prefix.Length + 4, first.Content.Headers.ContentLength);
        Assert.NotNull(activity.StreamFor(protocol, protocol.RequestedItemId, ProviderAudioQuality.Any));
        var seeking = gateway.OpenStreamAsync(protocol, "allstarr", protocol.RequestedItemId[17..],
            ProviderAudioQuality.Any, $"bytes={prefix.Length}-");
        if (changeEncoding)
        {
            var opened = await seeking.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(opened);
            Assert.Equal("qobuz", opened.ServingProviderId);
            using var resumed = opened.Response;
            Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
            Assert.Equal(new byte[] { 5, 6, 7, 8 }, await resumed.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, requests);
            other.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
            source.Release([1, 2, 3, 4]);
        }
        else
        {
            Assert.False(seeking.IsCompleted);
            source.Release([1, 2, 3, 4]);
            source.Complete();
            using var resumed = (await seeking.WaitAsync(TimeSpan.FromSeconds(10)))!.Response;
            Assert.Equal(HttpStatusCode.PartialContent, resumed.StatusCode);
            Assert.Equal($"bytes {prefix.Length}-{prefix.Length + 3}/{prefix.Length + 4}",
                resumed.Content.Headers.ContentRange!.ToString());
            Assert.Equal(4, resumed.Content.Headers.ContentLength);
            Assert.Contains("bytes", resumed.Headers.AcceptRanges);
            Assert.Equal(1, requests);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await resumed.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, requests);
        }
        source.Complete();
        await first.Content.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
