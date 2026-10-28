using System.Net;
using System.Net.Http.Headers;
using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.Monochrome;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Theory]
    [InlineData(true, true, ProviderUseMode.Automatic, 0, true, true)]
    [InlineData(false, true, ProviderUseMode.Automatic, 1, true, true)]
    [InlineData(false, false, ProviderUseMode.Automatic, 0, false, true)]
    [InlineData(false, true, ProviderUseMode.ManualOnly, 0, false, true)]
    [InlineData(false, true, ProviderUseMode.Off, 0, false, true)]
    [InlineData(false, true, ProviderUseMode.Automatic, 1, true, false)]
    public async Task MonochromePlayback_ReusesNativeIdentityOrPerformsOneExactLookup(
        bool native, bool hasIsrc, ProviderUseMode mode, int lookups, bool expected, bool ranges)
    {
        const string nativeId = "155142501534011392";
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "monochrome-recording", hasIsrc ? "USQX91300108" : null)).Recording;
        await Link(recording.Id, Context(actor, "spotiflac-tidal-web"), "tidal:258048607");
        await SeedAvailabilitySnapshot("tidal:258048607");
        if (native) await Link(recording.Id, Context(actor, "monochrome"), nativeId);
        var handler = new MonochromePlaybackHandler(ranges);
        using var http = new HttpClient(handler);
        var registry = new ProviderRegistry([MonochromeProviderRegistration.CreateRegistration(new(http))]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), null, It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "max", 0.07)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("monochrome", mode) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(_userA, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), _service, new ProviderRuntimeHealth(), policies.Object);
        using var activity = new PlaybackDeliveryActivityStore();
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            identities: _service, playbackActivity: activity, audioVerifier: FixtureAudioVerifier.Instance);
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", _userA.ToString("N"),
            new AllstarrPrincipal(_userA, "jellyfin", "fixture", _userA.ToString("N"), "User", false), "monochrome-playback",
            DateTimeOffset.UtcNow.AddMinutes(1), default, new("client", "device"))
        { Policy = policy };
        await gateway.SearchAsync(protocol, "Get Lucky", 10, 0, 0);
        Assert.Equal(0, handler.Searches);
        var opened = await gateway.OpenStreamAsync(protocol, "allstarr", recording.Id.ToString("D"), ProviderAudioQuality.Lossless, null);
        Assert.Equal(expected, opened != null);
        Assert.Equal(lookups, handler.Searches);
        if (opened == null) return;
        using (opened.Response)
        {
            Assert.Equal("monochrome", opened.ServingProviderId);
            Assert.Equal(ranges, opened.Lease.SupportsByteRanges);
            if (!ranges) Assert.Equal("180", Assert.Single(opened.Response.Headers.GetValues("X-Allstarr-Duration-Seconds")));
            Assert.Equal("fLaCfixture", await opened.Response.Content.ReadAsStringAsync());
        }
        if (!ranges) return;
        using var sought = (await gateway.OpenStreamAsync(protocol, "allstarr", recording.Id.ToString("D"),
            ProviderAudioQuality.Lossless, "bytes=4-9"))!.Response;
        Assert.Equal(HttpStatusCode.PartialContent, sought.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(4, 9, 11), sought.Content.Headers.ContentRange);
        Assert.Equal("fixtur", await sought.Content.ReadAsStringAsync());
        Assert.Equal(lookups, handler.Searches);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(nativeId, (await db.ProviderTrackIdentities.SingleAsync(item => item.ProviderId == "monochrome")).ExternalId);
    }

    [Theory]
    [InlineData("tidal:258048607")]
    [InlineData("45675112")]
    public async Task InvalidMonochromePlaybackLeavesNoRecordingOrMatch(string externalId)
    {
        using var http = new HttpClient(new MonochromePlaybackHandler(true));
        var registry = new ProviderRegistry([MonochromeProviderRegistration.CreateRegistration(new(http))]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), null, It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), _service);
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            identities: _service, audioVerifier: FixtureAudioVerifier.Instance);
        await Assert.ThrowsAsync<FileNotFoundException>(() => gateway.OpenStreamAsync(DisplayProtocol(ProtocolKind.Jellyfin), "monochrome", externalId, ProviderAudioQuality.Any, null));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.CanonicalRecordings.ToArrayAsync());
        Assert.Empty(await db.ProviderTrackIdentities.ToArrayAsync());
        Assert.Empty(await db.TrackMatches.ToArrayAsync());
        Assert.Empty(await db.ExternalMetadataSnapshots.ToArrayAsync());
    }

    [Fact]
    public async Task FailedAmazonPlaybackLeavesNoRecordingOrMatch()
    {
        var streaming = new Mock<IProviderStreamingCapability>();
        streaming.SetupGet(item => item.ProviderId).Returns("amazon");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.NotFound)));
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor("amazon", "Amazon Music", "Fixture",
            ProviderOrigin.BuiltIn, "1", "1.0", [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1.0", ["getStreamLease"])], new ProviderPermissionDescriptor()), [streaming.Object])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), null, It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), _service);
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            identities: _service, audioVerifier: FixtureAudioVerifier.Instance);
        await Assert.ThrowsAsync<FileNotFoundException>(() => gateway.OpenStreamAsync(DisplayProtocol(ProtocolKind.Jellyfin), "amazon", "B01M001FTH", ProviderAudioQuality.Any, null));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.CanonicalRecordings.ToArrayAsync());
        Assert.Empty(await db.ProviderTrackIdentities.ToArrayAsync());
        Assert.Empty(await db.TrackMatches.ToArrayAsync());
        Assert.Empty(await db.ExternalMetadataSnapshots.ToArrayAsync());
    }

    private sealed class MonochromePlaybackHandler(bool ranges) : HttpMessageHandler
    {
        public int Searches { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/search/tracks")
            {
                Searches++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"tracks":[{"trackId":"155142501534011392","isrc":"USQX91300108","playable":true}]}""") });
            }
            Assert.Equal("/track/155142501534011392", request.RequestUri.AbsolutePath);
            var bytes = "fLaCfixture"u8.ToArray();
            var range = ranges ? request.Headers.Range?.Ranges.Single() : null;
            var start = (int)(range?.From ?? 0);
            var end = (int)(range?.To ?? bytes.Length - 1);
            var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(bytes[start..(end + 1)]) };
            response.Content.Headers.ContentType = new("audio/flac");
            if (range != null) response.Content.Headers.ContentRange = new(start, end, bytes.Length);
            response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        }
    }
}
