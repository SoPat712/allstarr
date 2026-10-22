using System.Collections.Immutable;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Download", false)]
    [InlineData("Download", true)]
    [InlineData("File", false)]
    public async Task CanonicalOriginalUrlRangesUseTheOpeningCredentialWithoutPlaybackWrites(string endpoint, bool differentCredential)
    {
        const string canonical = "ext-allstarr-song-76d17d96-70e2-46b1-a3bf-358a6eb67e2b";
        byte[] bytes = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(x => x.ProviderId).Returns("qobuz");
        streaming.SetupGet(x => x.Capability).Returns(ProviderCapabilityKind.Streaming);
        streaming.Setup(x => x.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new("original", new Uri("https://media.example.test/original"),
                DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry, (request, _) =>
                {
                    var range = request.Headers.Range?.Ranges.Single();
                    var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent(range == null ? bytes : bytes[(int)range.From!.Value..((int)range.To!.Value + 1)]) };
                    if (range != null) response.Content.Headers.ContentRange = new(range.From!.Value, range.To!.Value, bytes.Length);
                    return Task.FromResult(response);
                })));
        var descriptor = new ProviderDescriptor("qobuz", "Qobuz", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getStreamLease"])], new());
        var registry = new ProviderRegistry([new(descriptor, [streaming.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(x => x.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderStreamingCapability>(request,
                [new(0, descriptor, descriptor.Capabilities.Single(), streaming.Object,
                    new(request.Actor, "qobuz", null, request.Policy, request.OperationId, request.CorrelationId,
                        request.Deadline, request.CancellationToken), new("qobuz", ProviderResourceKind.Track, "42"))],
                new(request.CorrelationId, ProviderCapabilityKind.Streaming, "qobuz", null, [])));
        using var activity = new PlaybackDeliveryActivityStore();
        var gateway = new ProtocolProviderGateway(router.Object, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict), playbackActivity: activity);
        using var factory = new ProtocolFactory("Jellyfin", _ => DownloadAuthentication("Jellyfin"), services =>
        {
            services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            policies.Setup(x => x.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                    ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                { UserId = user });
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton<IProtocolProviderGateway>(gateway);
        });
        using var client = factory.CreateClient();
        using (var initial = await client.GetAsync($"/Items/{canonical}/{endpoint}?api_key=fixture-key"))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            Assert.Equal(bytes, await initial.Content.ReadAsByteArrayAsync());
        }
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/Items/{canonical}/{endpoint}?api_key={(differentCredential ? "other-key" : "fixture-key")}");
        request.Headers.Range = new(24, 31);
        using var resumed = await client.SendAsync(request);
        Assert.Equal(differentCredential ? HttpStatusCode.Conflict : HttpStatusCode.PartialContent, resumed.StatusCode);
        if (!differentCredential)
        {
            Assert.Equal("bytes 24-31/32", resumed.Content.Headers.ContentRange!.ToString());
            Assert.Equal(8, resumed.Content.Headers.ContentLength);
            Assert.Equal(bytes[24..], await resumed.Content.ReadAsByteArrayAsync());
        }
        Assert.False(activity.WasStreamOpened(canonical));
    }
}
