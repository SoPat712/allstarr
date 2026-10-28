using System.Net;
using System.Collections.Immutable;
using allstarr.Core.Settings;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    public async Task ManualOnlyMetadataPreservesTheProviderItemWithoutCanonicalTranslation(ProtocolKind protocol, bool found)
    {
        var user = Guid.CreateVersion7();
        var context = new ProtocolExecutionContext(protocol, "backend", "listener",
            new AllstarrPrincipal(user, protocol.ToString().ToLowerInvariant(), "backend", "listener", "Listener", false),
            "chosen-metadata", DateTimeOffset.UtcNow.AddMinutes(1), default);
        context = context with
        {
            Policy = new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "High", 0.07)
            {
                UserId = context.RequireActor().EffectiveUserId,
                ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("deezer", ProviderUseMode.ManualOnly)
            }
        };
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns("deezer");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(found ? ProviderOutcome<ProviderTrackMetadata>.Success(
                new(new("deezer", ProviderResourceKind.Track, "chosen"), "Song", [new("Artist")])) :
                ProviderOutcome<ProviderTrackMetadata>.Failure(new(ProviderErrorKind.NotFound)));
        var registry = MetadataRegistry(metadata.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata.Object));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        var song = await gateway.GetSongAsync(context, "deezer", "chosen");

        if (found)
        {
            Assert.NotNull(song);
            Assert.Equal("ext-deezer-song-chosen", song.Id);
            Assert.Equal("deezer", song.ExternalProvider);
        }
        else Assert.Null(song);
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false, false)]
    [InlineData(ProtocolKind.Jellyfin, false, true)]
    [InlineData(ProtocolKind.Subsonic, false, false)]
    [InlineData(ProtocolKind.Subsonic, false, true)]
    [InlineData(ProtocolKind.Jellyfin, true, false)]
    [InlineData(ProtocolKind.Subsonic, true, false)]
    public async Task Playback_RetainsLiteralLegacyIdOnlyAfterSuccessfulNonHeadOpen(ProtocolKind protocol, bool headOnly, bool identityUnavailable)
    {
        var context = new ProtocolExecutionContext(protocol, "backend", "principal",
            new AllstarrPrincipal(Guid.CreateVersion7(), protocol.ToString().ToLowerInvariant(), "backend", "principal", "User", false),
            "identity-play", DateTimeOffset.UtcNow.AddMinutes(1), default,
            new ProtocolClientDescriptor("client", "device"))
        { RequestedItemId = "ext-qobuz-legacy-track" };
        var lease = new ProviderStreamLease("lease", new Uri("https://media.example.test/track"),
            DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.OK)));
        var capability = Capability("qobuz", ProviderOutcome<ProviderStreamLease>.Success(lease));
        var registry = Registry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, capability.Object));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var recording = new CanonicalRecordingIdentity(Guid.CreateVersion7(), context.RequireActor().EffectiveUserId!.Value,
            null, null, 1)
        { PublicId = context.RequestedItemId };
        if (!headOnly)
        {
            var remember = identities.Setup(item => item.RememberSourceAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderExternalResourceId>(id => id.ProviderId == "qobuz" && id.Value == "legacy-track"),
                context.RequestedItemId, ProviderIdentityScope.Catalog, null, It.IsAny<CancellationToken>()));
            if (identityUnavailable) remember.ThrowsAsync(new InvalidOperationException("Identity storage unavailable"));
            else remember.ReturnsAsync(recording.Id);
        }
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);
        var stream = await gateway.OpenStreamAsync(context, "qobuz", "legacy-track", ProviderAudioQuality.Any, null, headOnly);
        Assert.NotNull(stream);
        Assert.Equal(HttpStatusCode.OK, stream.Response.StatusCode);
        stream.Response.Dispose();
        identities.VerifyAll();
        if (headOnly) Assert.Empty(identities.Invocations);
    }
}
