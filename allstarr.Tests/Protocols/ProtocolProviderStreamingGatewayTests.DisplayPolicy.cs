using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProviderUseMode.Off)]
    [InlineData(ProviderUseMode.ManualOnly)]
    public async Task PublicRecordingDisplay_AppliesHouseholdSourcePolicyBeforeChoosingStoredMetadata(ProviderUseMode mode)
    {
        var policy = DisplayPolicyContext(ProtocolKind.Jellyfin, "apple-musickit", mode).Policy! with { UserId = null };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(It.Is<ProviderActorContext>(actor => actor.Kind == ProviderActorKind.PublicRead),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Song[]
            {
                new() { Id = "ext-deezer-song-song", Title = "Song", Artist = "Artist", ExternalProvider = "apple-musickit",
                    ExternalId = "preferred", Album = "Apple album", AlbumId = "ext-apple-download-album-release",
                    CoverArtUrl = "https://images.example.test/apple" },
                new() { Id = "ext-deezer-song-song", Title = "Song", Artist = "Artist", ExternalProvider = "deezer",
                    ExternalId = "song", Album = "Allowed album", AlbumId = "ext-deezer-album-release",
                    CoverArtUrl = "https://images.example.test/deezer" }
            });
        var gateway = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict), new ProviderRegistry([]),
            new HttpClientFactory(), identities: identities.Object, effectivePolicies: policies.Object, audioVerifier: FixtureAudioVerifier.Instance);

        var song = await gateway.GetPublicSongAsync("allstarr", Guid.NewGuid().ToString("D"));
        var artwork = await gateway.GetPublicArtworkUriAsync("allstarr", ProviderResourceKind.Track, Guid.NewGuid().ToString("D"));

        Assert.NotNull(song);
        Assert.Equal("deezer", song.ExternalProvider);
        Assert.Equal("Allowed album", song.Album);
        Assert.Equal(new Uri("https://images.example.test/deezer"), artwork);
        policies.Verify(item => item.ResolveAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        identities.VerifyAll();
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.Off)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.Off)]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.ManualOnly)]
    public async Task RecordingDisplay_ExcludesSourcesUnavailableForAutomaticUse(ProtocolKind protocol, ProviderUseMode mode)
    {
        var context = DisplayPolicyContext(protocol, "apple-musickit", mode);
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Song[]
            {
                new() { Id = "ext-deezer-song-song", Title = "Song", Artist = "Artist", ExternalProvider = "apple-musickit",
                    ExternalId = "preferred", Album = "Apple album", AlbumId = "ext-apple-download-album-release" },
                new() { Id = "ext-deezer-song-song", Title = "Song", Artist = "Artist", ExternalProvider = "deezer",
                    ExternalId = "song", Album = "Allowed album", AlbumId = "ext-deezer-album-release" }
            });
        var gateway = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict), new ProviderRegistry([]),
            new HttpClientFactory(), identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        var stored = await gateway.GetStoredSongAsync(context, "deezer", "song");
        var detail = await gateway.GetSongAsync(context, "deezer", "song");

        Assert.All(new[] { stored, detail }, song =>
        {
            Assert.NotNull(song);
            Assert.Equal("ext-deezer-song-song", song.Id);
            Assert.Equal("deezer", song.ExternalProvider);
            Assert.Equal("Allowed album", song.Album);
            Assert.Equal("ext-deezer-album-release", song.AlbumId);
        });
        identities.VerifyAll();
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task DisabledAlias_RefusesMetadataAndAudioBeforeAnyLookup(ProtocolKind protocol)
    {
        var context = DisplayPolicyContext(protocol, "deezer", ProviderUseMode.Off);
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, new ProviderRegistry([]), new HttpClientFactory(),
            applicationCache: cache.Object, identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        Assert.Null(await gateway.GetStoredSongAsync(context, "deezer", "song"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.GetSongAsync(context, "deezer", "song"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(context, "deezer", "song", ProviderAudioQuality.Any, null));

        identities.VerifyNoOtherCalls();
        router.VerifyNoOtherCalls();
        cache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task ManualOnlyDetail_ReusesOwnMetadataAndRechecksDisabledPolicy(ProtocolKind protocol)
    {
        var context = DisplayPolicyContext(protocol, "deezer", ProviderUseMode.ManualOnly);
        var metadata = SearchRankingCapability("deezer");
        Mock.Get(metadata).Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("deezer", ProviderResourceKind.Track, "song"),
                "Chosen title", [new("Chosen artist")], albumId: new("deezer", ProviderResourceKind.Album, "release"), albumTitle: "Chosen album")));
        var registry = MetadataRegistry(metadata);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        using var cache = new MemoryApplicationCache(new allstarr.Core.Operations.SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(),
            applicationCache: cache, identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var song = await gateway.GetSongAsync(context, "deezer", "song");
            Assert.NotNull(song);
            Assert.Equal("ext-deezer-song-song", song.Id);
            Assert.Null(song.CanonicalRecordingId);
            Assert.Equal("deezer", song.ExternalProvider);
            Assert.Equal("Chosen title", song.Title);
            Assert.Equal("Chosen album", song.Album);
            Assert.Equal("ext-deezer-album-release", song.AlbumId);
        }
        var disabled = context with { Policy = context.Policy! with { ProviderModes = context.Policy.ProviderModes.SetItem("deezer", ProviderUseMode.Off) } };
        Assert.Null(await gateway.GetStoredSongAsync(disabled, "deezer", "song"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.GetSongAsync(disabled, "deezer", "song"));
        Mock.Get(metadata).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Once);
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task ManualOnlyAlbum_CachedTracksKeepProviderMetadataWithoutIdentityReads(ProtocolKind protocol)
    {
        var context = DisplayPolicyContext(protocol, "deezer", ProviderUseMode.ManualOnly);
        var metadata = SearchRankingCapability("deezer");
        Mock.Get(metadata).Setup(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "release"),
                "Chosen album", [new("Chosen artist")], tracks: [new(new("deezer", ProviderResourceKind.Track, "song"),
                    "Chosen title", [new("Chosen artist")], albumId: new("deezer", ProviderResourceKind.Album, "release"), albumTitle: "Chosen album")])));
        var registry = MetadataRegistry(metadata);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        using var cache = new MemoryApplicationCache(new allstarr.Core.Operations.SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(),
            applicationCache: cache, identities: identities.Object, audioVerifier: FixtureAudioVerifier.Instance);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var song = Assert.Single((await gateway.GetAlbumAsync(context, "deezer", "release"))!.Songs);
            Assert.Equal("ext-deezer-song-song", song.Id);
            Assert.Null(song.CanonicalRecordingId);
            Assert.Equal("deezer", song.ExternalProvider);
            Assert.Equal("Chosen title", song.Title);
            Assert.Equal("Chosen album", song.Album);
            Assert.Equal("ext-deezer-album-release", song.AlbumId);
        }
        Mock.Get(metadata).Verify(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()), Times.Once);
        identities.VerifyNoOtherCalls();
    }

    private static ProtocolExecutionContext DisplayPolicyContext(ProtocolKind protocol, string provider, ProviderUseMode mode)
    {
        var context = SearchRankingContext(protocol);
        return context with
        {
            Policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            {
                UserId = context.RequireActor().EffectiveUserId,
                ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add(provider, mode)
            }
        };
    }
}
