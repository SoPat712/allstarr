using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using System.Text.Json;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task SongArtworkReusesTheCanonicalDisplayCoverForTheSameListener()
    {
        var context = Context();
        var recording = Guid.NewGuid();
        var song = new Song
        {
            Id = $"ext-allstarr-song-{recording:D}",
            CanonicalRecordingId = recording,
            Title = "Stored title",
            Artist = "Stored artist",
            Album = "Stored album",
            AlbumId = "ext-deezer-album-stored",
            ExternalProvider = "deezer",
            ExternalId = "stored",
            CoverArtUrl = "https://artwork.example.invalid/display.jpg"
        };
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(service => service.GetStoredSongsAsync(context.RequireActor(), song.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Song>)[song]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(), new HttpClientFactory(),
            identities: identities.Object, applicationCache: new TestMemoryApplicationCache());
        Assert.NotNull(await gateway.GetStoredSongAsync(context, "allstarr", recording.ToString("D")));
        identities.Invocations.Clear();
        Assert.Equal(song.CoverArtUrl, await gateway.GetStoredSongArtworkAsync(context, "allstarr", recording.ToString("N")));
        Assert.Null(await gateway.GetStoredSongArtworkAsync(Context(), "allstarr", recording.ToString("D")));
        identities.VerifyNoOtherCalls();
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SongArtworkReadsTheStoredSearchOrDetailCoverWithoutMatching(bool detail)
    {
        var context = Context();
        var cache = new TestMemoryApplicationCache();
        const string url = "https://artwork.example.invalid/search-cover.jpg";
        var song = new Song { Id = "ext-deezer-song-stored-art", ExternalProvider = "deezer", ExternalId = "stored-art", CoverArtUrl = url };
        await cache.SetStringAsync(ProtocolProviderGateway.MetadataCacheKey(context.RequireActor(), "deezer",
            ProviderResourceKind.Track, "stored-art", detail), JsonSerializer.Serialize(song));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(), new HttpClientFactory(),
            identities: identities.Object, applicationCache: cache);
        Assert.Equal(url, await gateway.GetStoredSongArtworkAsync(context, "deezer", "stored-art"));
        router.VerifyNoOtherCalls();
        identities.VerifyNoOtherCalls();
        Assert.Null(await gateway.GetStoredSongArtworkAsync(context, "allstarr", "stored-art"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicTrack_UsesOnlyTypedPublicMetadataForActivityAndUnlinkedPrincipals(bool protocolLookup)
    {
        const string provider = "apple-musickit";
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        ProviderExecutionContext? execution = null;
        capability.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderTrackLookupRequest>(request => request.Id.ProviderId == provider && request.Id.Value == "track")))
            .Callback((ProviderExecutionContext context, ProviderTrackLookupRequest _) => execution = context)
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(
                new(provider, ProviderResourceKind.Track, "track"), "Track",
                [new("Artist", new(provider, ProviderResourceKind.Artist, "artist"))],
                albumId: new(provider, ProviderResourceKind.Album, "album"), albumTitle: "Album")));
        var registry = MetadataRegistry(capability.Object);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(provider, null, ProviderCapabilityKind.Metadata))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, false));
        var router = new ProviderRouter(registry, accounts.Object, health.Object,
            Mock.Of<IProviderRouteSidecarSource>(MockBehavior.Strict), Mock.Of<ITrackIdentityService>(MockBehavior.Strict));
        var gateway = new ProtocolProviderGateway(router, registry, new HttpClientFactory(), audioVerifier: FixtureAudioVerifier.Instance);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "unlinked", null,
            "public-track", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        var song = protocolLookup
            ? await gateway.GetSongAsync(context, "apple-download", "track")
            : await gateway.GetPublicSongAsync("applemusic", "track");

        Assert.NotNull(song);
        Assert.Equal("ext-apple-download-song-track", song.Id);
        Assert.NotNull(execution);
        Assert.Equal(ProviderActorKind.PublicRead, execution.Actor.Kind);
        Assert.Null(execution.Account);
        Assert.False(execution.Policy.AllowSharedAccount);
        accounts.VerifyNoOtherCalls();
        capability.VerifyAll();
    }

    [Theory]
    [InlineData(ProviderResourceKind.Track)]
    [InlineData(ProviderResourceKind.Album)]
    [InlineData(ProviderResourceKind.Artist)]
    [InlineData(ProviderResourceKind.Playlist)]
    public async Task PublicArtwork_UsesStoredReferencesWithoutProviderMetadata(ProviderResourceKind kind)
    {
        var artwork = new Uri("https://artwork.example.invalid/cover.jpg");
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<allstarr.Models.Domain.Song>());
        if (kind != ProviderResourceKind.Playlist)
            identities.Setup(item => item.GetStoredArtworkUriAsync(
                    new ProviderExternalResourceId("apple-musickit", kind, "public-resource"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(artwork);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, new ProviderRegistry([]), new HttpClientFactory(),
            identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        var result = await gateway.GetPublicArtworkUriAsync("applemusic", kind, "public-resource");

        Assert.Equal(kind == ProviderResourceKind.Playlist ? null : artwork, result);
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProviderAccountRequirement.Optional)]
    [InlineData(ProviderAccountRequirement.Required)]
    public async Task PublicArtwork_NeverEntersAccountBoundMetadata(ProviderAccountRequirement requirement)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("private");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(
            "private", "Private", "Account-bound metadata", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, requirement,
                "1", ["searchTracks", "getTrack"], [ProviderAccountScope.Personal, ProviderAccountScope.Shared])],
            new ProviderPermissionDescriptor()), [capability.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), audioVerifier: FixtureAudioVerifier.Instance);

        Assert.Null(await gateway.GetPublicArtworkUriAsync("private", ProviderResourceKind.Track, "private-resource"));
        Assert.Null(await gateway.GetPublicSongAsync("private", "private-resource"));
        router.VerifyNoOtherCalls();
        accounts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublicArtwork_RespectsDisabledProvidersBeforeLookup()
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Disabled"] = "deezer"
        }).Build();
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(capability.Object),
            new HttpClientFactory(), configuration, audioVerifier: FixtureAudioVerifier.Instance);

        Assert.Null(await gateway.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "track"));
        Assert.Null(await gateway.GetPublicSongAsync("deezer", "track"));
        router.VerifyNoOtherCalls();
    }
}
