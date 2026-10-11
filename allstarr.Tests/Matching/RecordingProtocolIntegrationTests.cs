using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task MergedGeneratedId_ResolvesMetadataAndPlaysWithoutRevivingTheLoser()
    {
        var first = await _service.CreateRecordingAsync(Actor(_userA), "first", null, null, "ext-deezer-first");
        var loser = await _service.CreateRecordingAsync(Actor(_userA), "second");
        await Link(loser.Recording.Id, Context(Actor(_userA), "qobuz"), "available");
        await _service.MergeRecordingsAsync(Actor(_userA), first.Recording.Id, loser.Recording.Id, "merge");
        var restarted = new TrackIdentityService(_factory, _storageState, _clock);
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.SetupGet(item => item.ProviderId).Returns("qobuz");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderTrackLookupRequest>(request => request.Id.Value == "available")))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("qobuz", ProviderResourceKind.Track, "available"),
                "Track", [new("Artist", new("qobuz", ProviderResourceKind.Artist, "artist"))],
                artwork: new ProviderArtworkReference(publicUri: new Uri("https://art.example.test/cover")))));
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(item => item.ProviderId).Returns("qobuz");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "available")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new("lease", new Uri("https://media.example.test/track"),
                DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x66, 0x4c, 0x61, 0x43])
                }))));
        var registry = new ProviderRegistry([new ProviderRegistration(new("qobuz", "Qobuz", "Fixture", ProviderOrigin.BuiltIn,
            "1", "1", [
                new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"]),
                new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getStreamLease"])
            ], new()), [metadata.Object, streaming.Object])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(MockBehavior.Strict), restarted);
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict), identities: restarted);
        var protocol = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", _userA.ToString("N"),
            new AllstarrPrincipal(_userA, "jellyfin", "fixture", _userA.ToString("N"), "User", false), "merged-play",
            DateTimeOffset.UtcNow.AddMinutes(1), default, new ProtocolClientDescriptor("client", "device"));

        var song = await gateway.GetSongAsync(protocol, "allstarr", loser.Recording.Id.ToString("D"));
        Assert.Equal(first.Recording.PublicId, song?.Id);
        Assert.Equal(first.Recording.Id, song?.CanonicalRecordingId);
        var publicSong = await gateway.GetPublicSongAsync("allstarr", loser.Recording.Id.ToString("D"));
        Assert.Equal(first.Recording.PublicId, publicSong?.Id);
        Assert.Equal(new Uri("https://art.example.test/cover"), await gateway.GetPublicArtworkUriAsync(
            "allstarr", ProviderResourceKind.Track, loser.Recording.Id.ToString("D")));
        var stream = await gateway.OpenStreamAsync(protocol, "allstarr", loser.Recording.Id.ToString("D"), ProviderAudioQuality.Any, null);
        Assert.NotNull(stream);
        Assert.Equal("available", stream.ServingExternalId);
        Assert.Equal("qobuz", stream.ServingProviderId);
        Assert.Equal(new byte[] { 0x66, 0x4c, 0x61, 0x43 }, await stream.Response.Content.ReadAsByteArrayAsync());
        stream.Response.Dispose();
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.CanonicalRecordings.CountAsync());
        Assert.Equal(first.Recording.Id, (await verify.CanonicalRecordings.SingleAsync(item => item.Id == loser.Recording.Id)).MergedIntoId);
        streaming.VerifyAll();
    }
}
