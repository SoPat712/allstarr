using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolPlaylistGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PlaylistSearch_UsesResolvedActorAccountAndDoesNotQueryLegacyProviders(
        ProtocolKind protocolKind)
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var context = Context(protocolKind, tenant, user);
        var capability = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("spotify");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        capability.Setup(item => item.SearchPlaylistsAsync(
                It.Is<ProviderExecutionContext>(execution =>
                    execution.Actor.TenantId == tenant &&
                    execution.Actor.EffectiveUserId == user &&
                    execution.Account != null && execution.Account.AccountId == accountId),
                It.Is<ProviderPlaylistSearchRequest>(request => request.Query == "road")))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new(
                "spotify",
                [PlaylistSummary()])));
        var descriptor = Descriptor(hasImplementation: true);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(
                request,
                descriptor,
                capability.Object,
                new ProviderAccountContext(
                    accountId, "spotify", ProviderAccountScope.User, 1,
                    tenantId: tenant, ownerUserId: user)));
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var gateway = Gateway(router.Object, Registry(descriptor, capability.Object), legacy.Object);

        var result = await gateway.SearchPlaylistsAsync(context, "road", 10);

        Assert.Equal("Playlist", Assert.Single(result).Name);
        legacy.VerifyNoOtherCalls();
        capability.VerifyAll();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PlaylistSearch_ReturnsEmptyWhileLegacyExternalPlaylistsAreDisabled(
        ProtocolKind protocolKind)
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var context = Context(protocolKind, tenant, user);
        var capability = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("spotify");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        capability.Setup(item => item.SearchPlaylistsAsync(
                It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderPlaylistSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(
                new(ProviderErrorKind.AccountNeedsReauthentication)));
        var descriptor = Descriptor(hasImplementation: true);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(
                request,
                descriptor,
                capability.Object,
                new ProviderAccountContext(
                    accountId, "spotify", ProviderAccountScope.User, 1,
                    tenantId: tenant, ownerUserId: user)));
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var gateway = Gateway(router.Object, Registry(descriptor, capability.Object), legacy.Object);

        var playlists = await gateway.SearchPlaylistsAsync(context, "road", 10);

        Assert.Empty(playlists);
        legacy.VerifyNoOtherCalls();
        capability.VerifyAll();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PlaylistRead_UsesExactResolvedActorAccountAndNeverLegacy(ProtocolKind protocolKind)
    {
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var context = Context(protocolKind, tenant, user);
        var capability = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("spotify");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        capability.Setup(item => item.GetPlaylistTracksAsync(
                It.Is<ProviderExecutionContext>(execution =>
                    execution.Actor.TenantId == tenant &&
                    execution.Actor.EffectiveUserId == user &&
                    execution.Account != null && execution.Account.AccountId == accountId),
                It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(PlaylistPage());

        var descriptor = Descriptor(hasImplementation: true);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(
                request,
                descriptor,
                capability.Object,
                new ProviderAccountContext(
                    accountId, "spotify", ProviderAccountScope.User, 1,
                    tenantId: tenant, ownerUserId: user)));
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var gateway = Gateway(router.Object, Registry(descriptor, capability.Object), legacy.Object);

        var result = await gateway.GetPlaylistTracksAsync(context, "spotify", "playlist-1");

        var track = Assert.Single(result);
        Assert.Equal("Track", track.Title);
        Assert.Equal("ext-spotify-song-track-1", track.Id);
        Assert.Equal("ext-spotify-album-album-1", track.AlbumId);
        Assert.Equal("ext-spotify-artist-artist-1", track.ArtistId);
        Assert.Equal(["ext-spotify-artist-artist-1"], track.ArtistIds);
        Assert.Equal("https://images.example.test/album-1.webp", track.CoverArtUrl);
        legacy.VerifyNoOtherCalls();
        capability.VerifyAll();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task PlaylistRead_RejectsUnavailableUserRouteWithoutCrossUserLegacyFallback(
        ProtocolKind protocolKind)
    {
        var context = Context(protocolKind, Guid.CreateVersion7(), Guid.CreateVersion7());
        var descriptor = Descriptor(hasImplementation: true);
        var registeredCapability = new Mock<IProviderPlaylistCapability>();
        registeredCapability.SetupGet(item => item.ProviderId).Returns("spotify");
        registeredCapability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderPlaylistCapability>(
                request,
                [],
                new ProviderRouteDecisionRecord(
                    request.CorrelationId,
                    ProviderCapabilityKind.Playlist,
                    null,
                    null,
                    [new("spotify", null, ProviderRouteDecisionStatus.Rejected, "account-not-authorized", 0)])));
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var gateway = Gateway(router.Object, Registry(descriptor, registeredCapability.Object), legacy.Object);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.GetPlaylistAsync(context, "spotify", "playlist-1"));

        legacy.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task UnresolvedPrincipal_CannotProbeOrReadProviderPlaylists(
        ProtocolKind protocolKind)
    {
        var context = new ProtocolExecutionContext(
            protocolKind,
            "backend",
            "principal",
            null,
            "playlist-unresolved",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var capability = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("spotify");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        var gateway = Gateway(router.Object,
            Registry(Descriptor(hasImplementation: true), capability.Object), legacy.Object);

        Assert.Empty(await gateway.SearchPlaylistsAsync(context, "private", 10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.GetPlaylistAsync(context, "spotify", "playlist-1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.GetPlaylistTracksAsync(context, "spotify", "playlist-1"));

        legacy.VerifyNoOtherCalls();
        router.VerifyNoOtherCalls();
    }

    private static ProtocolProviderGateway Gateway(
        IProviderRouter router,
        IProviderRegistry registry,
        IMusicMetadataService legacy) => new(
        router,
        registry,
        new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict).Object,
        legacy,
        new Mock<IHttpClientFactory>(MockBehavior.Strict).Object);

    private static ProtocolExecutionContext Context(
        ProtocolKind protocol,
        Guid tenant,
        Guid user)
    {
        var backend = protocol.ToString().ToLowerInvariant();
        return new(
            protocol,
            "backend",
            "principal",
            new AllstarrPrincipal(tenant, user, backend, "backend", "principal", "User", false),
            "playlist-test",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
    }

    private static ProviderOutcome<ProviderPlaylistTrackPage> PlaylistPage()
    {
        var trackId = new ProviderExternalResourceId(
            "spotify", ProviderResourceKind.Track, "track-1");
        var summary = PlaylistSummary();
        var metadata = new ProviderTrackMetadata(
            trackId,
            "Track",
            [new ProviderArtistCredit(
                "Artist",
                new ProviderExternalResourceId("spotify", ProviderResourceKind.Artist, "artist-1"))],
            new ProviderExternalResourceId("spotify", ProviderResourceKind.Album, "album-1"),
            "Album",
            artwork: new ProviderArtworkReference(
                publicUri: new Uri("https://images.example.test/album-1.webp")));
        return ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(
            summary,
            new ProviderPage<ProviderPlaylistTrack>(
                "spotify", [new ProviderPlaylistTrack(0, trackId, metadata: metadata)])));
    }

    private static ProviderPlaylistSummary PlaylistSummary() => new(
        new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "playlist-1"),
        "Playlist",
        new ProviderPlaylistOwner("owner"),
        "revision");

    private static ProviderRoutePlan<IProviderPlaylistCapability> Plan(
        ProviderRouteRequest request,
        ProviderDescriptor provider,
        IProviderPlaylistCapability capability,
        ProviderAccountContext account)
    {
        var descriptor = provider.Capabilities.Single();
        var execution = new ProviderExecutionContext(
            request.Actor,
            provider.Id,
            account,
            null,
            request.Policy,
            request.OperationId,
            request.CorrelationId,
            request.Deadline,
            request.CancellationToken);
        var candidate = new ProviderRouteCandidate<IProviderPlaylistCapability>(
            0, provider, descriptor, capability, execution, null);
        return new(
            request,
            [candidate],
            new ProviderRouteDecisionRecord(
                request.CorrelationId,
                ProviderCapabilityKind.Playlist,
                provider.Id,
                account.AccountId,
                [new(provider.Id, account.AccountId, ProviderRouteDecisionStatus.Accepted, "selected", 0)]));
    }

    private static IProviderRegistry Registry(
        ProviderDescriptor descriptor,
        IProviderPlaylistCapability? capability = null) => new ProviderRegistry([
        new ProviderRegistration(descriptor, capability == null ? [] : [capability])
    ]);

    private static ProviderDescriptor Descriptor(bool hasImplementation) => new(
        "spotify",
        "Spotify",
        "Spotify test provider",
        ProviderOrigin.BuiltIn,
        "1",
        "1.0",
        [new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Playlist,
            ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.Required,
            "1.0",
            hasImplementation ? ["getUserPlaylists", "searchPlaylists", "getPlaylistTracks"] : [],
            [ProviderAccountScope.User])],
        new ProviderPermissionDescriptor());
}
