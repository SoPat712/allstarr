using System.Net;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Playlists;
using Microsoft.Extensions.DependencyInjection.Extensions;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_ServiceMixArtworkUsesOnlyTheSourceCoverAndCachesIt(bool synced)
    {
        var capability = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        var playlist = new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly");
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                [new(playlist, "Discover Weekly", new("spotify"), "revision", artwork: new(playlist, revision: "cover"), trackCount: 30)])));
        var picture = MixPicture();
        Guid owner = default, account = default;
        capability.Setup(item => item.ResolveArtworkAsync(It.Is<ProviderExecutionContext>(context => context.Actor.EffectiveUserId == owner && context.Account!.AccountId == account),
                It.Is<ProviderPlaylistArtworkRequest>(request => request.Artwork.ResourceId == playlist && request.Artwork.Revision == "cover" && request.MaximumBytes == 4 * 1024 * 1024)))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistArtwork>.Success(new(picture, "image/png")));
        var registry = new Mock<IProviderRegistry>();
        IProviderPlaylistCapability? implementation = capability.Object;
        registry.Setup(item => item.TryGetCapability("spotify", ProviderCapabilityKind.Playlist, out implementation)).Returns(true);
        ProviderDescriptor? descriptor = new("spotify", "Spotify", "Playlist fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1", ["getUserPlaylists"], [ProviderAccountScope.Personal])], new());
        registry.Setup(item => item.TryGet("spotify", out descriptor)).Returns(true);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        using var factory = ClientMixFactory(gateway, configure: services =>
        {
            services.RemoveAll<IProviderRegistry>();
            services.AddSingleton(registry.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        account = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = owner, ProviderId = "spotify", Enabled = true, Revision = 1 });
            if (synced)
            {
                db.PlaylistLinks.Add(new()
                {
                    Id = linkId,
                    OwnerUserId = owner,
                    ProviderAccountId = account,
                    Enabled = true,
                    SourceProviderId = "spotify",
                    SourcePlaylistId = playlist.Value,
                    SourcePlaylistIdHash = new('a', 64),
                    TargetProtocol = "jellyfin",
                    TargetBackendInstanceId = "primary",
                    Mode = PlaylistLinkMode.Virtual,
                    RuleVersion = "1",
                    PolicyVersion = "1"
                });
                db.PlaylistSourceSnapshots.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    PlaylistLinkId = linkId,
                    ProviderAccountId = account,
                    SnapshotVersion = 1,
                    ProviderRevision = "revision",
                    Name = "Discover Weekly",
                    SourceKind = PlaylistSourceKinds.Feed,
                    ArtworkReferenceKey = "source-cover",
                    PayloadSha256 = new('b', 64),
                    CorrelationId = "fixture",
                    PublishedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (synced)
        {
            var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("kind").GetString() == "external");
            Assert.True(card.GetProperty("inApps").GetBoolean());
            Assert.Equal(30, card.GetProperty("itemCount").GetInt32());
            var cover = card.GetProperty("coverUrl").GetString()!;
            Assert.StartsWith("/allstarr/v1/mixes/", cover);
            Assert.Equal(cover, Assert.Single(card.GetProperty("artworkUrls").EnumerateArray()).GetString());
            using var image = await client.GetAsync(cover);
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
            Assert.Equal(picture, await image.Content.ReadAsByteArrayAsync());
            using var conditional = new HttpRequestMessage(HttpMethod.Get, cover);
            conditional.Headers.IfNoneMatch.Add(image.Headers.ETag!);
            using var cached = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            capability.Verify(item => item.ResolveArtworkAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistArtworkRequest>()), Times.Once);
            capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Never);
            gateway.Verify(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
            gateway.VerifyNoOtherCalls();
        }
        else
        {
            Assert.DoesNotContain(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("kind").GetString() == "external");
            var unadded = (await factory.Services.GetRequiredService<PersonalMixService>().ListCardsAsync(owner)).Single(mix => mix.Kind == "external");
            using var coverResponse = await client.GetAsync($"/allstarr/v1/mixes/{unadded.Id}/cover");
            Assert.Equal(HttpStatusCode.NotFound, coverResponse.StatusCode);
            capability.Verify(item => item.ResolveArtworkAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistArtworkRequest>()), Times.Never);
            capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()), Times.Never);
            gateway.Verify(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()), Times.Never);
            gateway.VerifyNoOtherCalls();
        }
    }

    [Theory]
    [InlineData("\"ImageTags\":{\"Primary\":\"song art\"},\"AlbumPrimaryImageTag\":\"album-art\"", "/Items/native-mix-song/Images/Primary?tag=song%20art")]
    [InlineData("\"ImageTags\":{},\"AlbumPrimaryImageTag\":\"album-art\"", "/Items/native-album/Images/Primary?tag=album-art")]
    [InlineData("\"ImageTags\":{}", null)]
    public async Task ClientApi_MixSummaryArtworkMatchesItemArtworkWithoutProviderLookups(string nativeArtwork, string? expected)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = ClientMixFactory(gateway, nativeArtwork: nativeArtwork);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = body.RootElement.GetProperty("mixes").EnumerateArray().ToArray();
        var card = Assert.Single(cards, row => row.GetProperty("mixId").GetString() == mix.Id.ToString());
        var external = "/Items/ext-deezer-song-mix-song/Images/Primary?tag=ext-deezer-song-mix-song-art-v3";
        Assert.Equal(expected == null ? [external] : new[] { external, expected },
            card.GetProperty("artworkUrls").EnumerateArray().Select(url => url.GetString()));
        Assert.All(cards.Where(row => row.GetProperty("itemCount").GetInt32() == 0),
            row => Assert.Empty(row.GetProperty("artworkUrls").EnumerateArray()));
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClientApi_MixSummaryArtworkIsEmptyWhenLibrarySongsHaveNoImages()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = ClientMixFactory(gateway, nativeArtwork: "\"ImageTags\":{}");
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory, serviceSong: new() { Id = "native-mix-song", IsLocal = true, ExternalProvider = "library" });
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("mixId").GetString() == mix.Id.ToString());
        Assert.Equal(1, card.GetProperty("itemCount").GetInt32());
        Assert.Empty(card.GetProperty("artworkUrls").EnumerateArray());
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClientApi_MixSummaryArtworkKeepsImageOrderAndTakesFourDistinctImages()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var nativeItems = "\"ImageTags\":{},\"AlbumPrimaryImageTag\":\"album-art\"";
        using var factory = ClientMixFactory(gateway, nativeArtwork: nativeItems, duplicateAlbum: true);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
        var document = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        Song External(string id) => new() { Id = "ext-deezer-song-" + id, ExternalProvider = "deezer", ExternalId = id };
        Song[] songs = [new() { Id = "native-mix-song", IsLocal = true }, External("first"),
            new() { Id = "native-second-song", IsLocal = true }, External("first"), External("second"), External("third"), External("fourth")];
        document.Mixes["custom"] = mix with { Songs = songs.Select(song => new PersonalMixItem(mix.Id, 2, song, "library", "Picked for you", 1)).ToArray() };
        row.Id = Guid.NewGuid(); row.SnapshotVersion++;
        row.PayloadJson = JsonSerializer.Serialize(document);
        db.ExternalMetadataSnapshots.Add(row);
        db.LibraryTracks.Add(new() { Id = Guid.NewGuid(), OwnerUserId = row.OwnerUserId, Protocol = "jellyfin", BackendInstanceId = "primary", BackendItemId = "native-second-song", BackendLibraryId = "library", Revision = 1 });
        await db.SaveChangesAsync();
        using var response = await client.GetAsync("/allstarr/v1/mixes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var card = Assert.Single(body.RootElement.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("mixId").GetString() == mix.Id.ToString());
        Assert.Equal(new[] { "/Items/native-album/Images/Primary?tag=album-art",
            "/Items/ext-deezer-song-first/Images/Primary?tag=ext-deezer-song-first-art-v3",
            "/Items/ext-deezer-song-second/Images/Primary?tag=ext-deezer-song-second-art-v3",
            "/Items/ext-deezer-song-third/Images/Primary?tag=ext-deezer-song-third-art-v3" },
            card.GetProperty("artworkUrls").EnumerateArray().Select(url => url.GetString()));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ext-deezer-album-mix-album")]
    [InlineData(null)]
    [InlineData("")]
    public async Task ClientApi_MixItemsUseStoredRecordingAlbumAndLibraryAlbumArtwork(string? albumId)
    {
        var song = ClientMixSong();
        song.Id = "ext-allstarr-song-recording";
        song.AlbumId = null;
        var metadata = ClientMixSong();
        metadata.Id = song.Id;
        metadata.AlbumId = albumId;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", "recording"))
            .ReturnsAsync(metadata);
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory, serviceSong: song, includeLibrary: true);

        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var external = items[0];
        var jellyfin = new JellyfinResponseBuilder().ConvertSongToJellyfinItem(metadata);
        Assert.Equal(string.IsNullOrWhiteSpace(albumId) ? null : jellyfin["AlbumId"], external.GetProperty("albumId").GetString());
        Assert.Equal(song.Id, external.GetProperty("itemId").GetString());
        var tag = ((Dictionary<string, string>)jellyfin["ImageTags"]!)["Primary"];
        Assert.Equal($"/Items/{song.Id}/Images/Primary?tag={tag}", external.GetProperty("artworkUrl").GetString());
        Assert.Equal("native-album", items[1].GetProperty("albumId").GetString());
        Assert.Equal("/Items/native-album/Images/Primary?tag=native-album-art", items[1].GetProperty("artworkUrl").GetString());
        gateway.Verify(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", "recording"), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ClientApi_MixItemsKeepKnownAlbumWhenStoredSourceHasNoRelationship(string? albumId)
    {
        var metadata = ClientMixSong();
        metadata.AlbumId = albumId;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(metadata);
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory, serviceSong: ClientMixSong());

        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("ext-deezer-album-mix-album", item.GetProperty("albumId").GetString());
        Assert.Equal("/Items/ext-deezer-song-mix-song/Images/Primary?tag=ext-deezer-song-mix-song-art-v3", item.GetProperty("artworkUrl").GetString());
        gateway.Verify(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("\"ImageTags\":{\"Primary\":\"song art\"},\"AlbumPrimaryImageTag\":\"album-art\"", "/Items/native-mix-song/Images/Primary?tag=song%20art")]
    [InlineData("\"ImageTags\":{},\"AlbumPrimaryImageTag\":\"album-art\"", "/Items/native-album/Images/Primary?tag=album-art")]
    [InlineData("\"ImageTags\":{\"Primary\":\"\"},\"AlbumPrimaryImageTag\":\"album-art\"", "/Items/native-album/Images/Primary?tag=album-art")]
    [InlineData("\"ImageTags\":{}", null)]
    public async Task ClientApi_MixItemsPreferLibraryItemArtworkThenKnownAlbumArtwork(string nativeArtwork, string? expected)
    {
        using var factory = ClientMixFactory(new(), nativeArtwork: nativeArtwork);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);

        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray(), row => !row.GetProperty("isExternal").GetBoolean());
        Assert.Equal("native-album", item.GetProperty("albumId").GetString());
        Assert.Equal(expected, item.GetProperty("artworkUrl").GetString());
    }
}
