using System.Net;
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
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task Playback_RetainsLiteralLegacyIdOnlyAfterSuccessfulNonHeadOpen(ProtocolKind protocol, bool headOnly)
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
            identities.Setup(item => item.RememberSourceAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderExternalResourceId>(id => id.ProviderId == "qobuz" && id.Value == "legacy-track"),
                context.RequestedItemId, ProviderIdentityScope.Catalog, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(recording.Id);
        }
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object);
        var stream = await gateway.OpenStreamAsync(context, "qobuz", "legacy-track", ProviderAudioQuality.Any, null, headOnly);
        Assert.NotNull(stream);
        Assert.Equal(HttpStatusCode.OK, stream.Response.StatusCode);
        stream.Response.Dispose();
        identities.VerifyAll();
        if (headOnly) Assert.Empty(identities.Invocations);
    }
}
