using System.Net;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_Releases_refresh_library_artist_names_picture_tags_and_changed_identifiers_once_per_window()
    {
        var metadata = ClientReleaseMetadata();
        var oldMbid = Guid.NewGuid().ToString();
        var newMbid = Guid.NewGuid().ToString();
        metadata.Setup(value => value.GetArtistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistLookupRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderArtistLookupRequest request) => ProviderOutcome<ProviderArtistMetadata>.Success(
                new(request.Id, "Release artist", providerIds: new Dictionary<string, string> { ["MusicBrainzArtist"] = newMbid })));
        var tag = "new-picture";
        var nativeReads = 0;
        using var factory = ClientReleaseFactory(metadata, new(), library: request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/listener/Items/native-release-artist")
            {
                nativeReads++;
                return Json(200, JsonSerializer.Serialize(new
                {
                    Id = "native-release-artist",
                    Name = "Release artist renamed",
                    Type = "MusicArtist",
                    ImageTags = new { Primary = tag },
                    ProviderIds = new { MusicBrainzArtist = newMbid }
                }));
            }
            if (request.RequestUri.AbsolutePath == "/Users/listener/Items") return Json(200,
                request.RequestUri.Query.Contains("ArtistIds=", StringComparison.Ordinal)
                    ? """{"Items":[{"Name":"New record","Type":"MusicAlbum"}]}""" : """{"Items":[]}""");
            return null;
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(owner, new("deezer", "old-artist", "Old name", oldMbid,
            LibraryArtistId: "native-release-artist", LibraryImageTag: "old-picture", FromCatalogEvidence: true, LibraryMusicBrainzArtistId: oldMbid));
        using var first = await client.GetAsync("/allstarr/v1/releases");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var refreshed = Assert.Single(await releases.ListAsync(owner));
        Assert.Equal("Release artist renamed", refreshed.DisplayName);
        Assert.Equal("new-picture", refreshed.LibraryImageTag);
        Assert.Equal(newMbid, refreshed.LibraryMusicBrainzArtistId);
        Assert.Equal("release-artist", refreshed.ArtistId);
        tag = "later-picture";
        using var second = await client.GetAsync("/allstarr/v1/releases");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, nativeReads);
        Assert.Equal("new-picture", Assert.Single(await releases.ListAsync(owner)).LibraryImageTag);
    }

    [Fact]
    public async Task ClientApi_LibraryIndex_resolves_release_follows_and_similar_album_artists_before_search()
    {
        var metadata = ClientReleaseMetadata();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetProviderOrder(ProviderCapabilityKind.Metadata)).Returns(["deezer"]);
        gateway.Setup(value => value.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "verified-artist", It.IsAny<int>()))
            .ReturnsAsync([]);
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(value => value.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid owner, CancellationToken token) => new BackendLibraryAccessContext(new(ProtocolKind.Jellyfin, "primary", "listener",
                new(owner, "jellyfin", "primary", "listener", "Listener", false), "artist-index", DateTimeOffset.UtcNow.AddMinutes(10), token), new(true, ["music"])));
        using var factory = ClientReleaseFactory(metadata, gateway, library: request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/listener/Items/indexed-album" => Json(200, """{"Id":"indexed-album","Name":"Owned album","Type":"MusicAlbum","AlbumArtist":"Release artist"}"""),
            _ => null
        }, configure: services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            for (var number = 0; number < 2; number++)
            {
                var recording = Guid.CreateVersion7();
                var identity = Guid.CreateVersion7();
                var track = "indexed-song-" + number;
                var hash = CanonicalCatalogKeys.Hash(track);
                db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, Title = track });
                db.LibraryTracks.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    CanonicalRecordingId = recording,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendLibraryId = "music",
                    BackendItemId = track,
                    FilePath = "/music/" + track,
                    Title = track,
                    Artist = "Release artist"
                });
                db.ProviderTrackIdentities.Add(new()
                {
                    Id = identity,
                    CanonicalRecordingId = recording,
                    ProviderId = "deezer",
                    ResourceKind = ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Catalog,
                    ExternalId = track,
                    ExternalIdHash = hash,
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "availability",
                    DecisionVersion = 1
                });
                db.ExternalMetadataSnapshots.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    ProviderId = "deezer",
                    ResourceKind = "track",
                    ProviderTrackIdentityId = identity,
                    ExternalIdHash = hash,
                    SnapshotVersion = 1,
                    ProviderRevision = "1",
                    PayloadJson = JsonSerializer.Serialize(new ProviderTrackMetadata(new("deezer", ProviderResourceKind.Track, track), track,
                        [new("Release artist", new("deezer", ProviderResourceKind.Artist, "verified-artist"))])),
                    PayloadSha256 = new string('a', 64),
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendPrincipalId = "listener"
                });
            }
            await db.SaveChangesAsync();
        }
        using var follow = await client.PutAsync("/allstarr/v1/artists/native-release-artist/follow", null);
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        Assert.Equal("verified-artist", Assert.Single(await factory.Services.GetRequiredService<NewReleasesService>().ListAsync(owner)).ArtistId);
        using var similar = await client.GetAsync("/allstarr/v1/items/indexed-album/similar");
        Assert.Equal(HttpStatusCode.OK, similar.StatusCode);
        gateway.Verify(value => value.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "verified-artist", It.IsAny<int>()), Times.Once);
        gateway.Verify(value => value.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        metadata.Verify(value => value.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
    }

    [Fact]
    public async Task ClientApi_ReleasesMatchInboxAndFollowedArtistsAndCoverUsesTheSameStoredArtwork()
    {
        var metadata = ClientReleaseMetadata();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release-album"))
            .ReturnsAsync(new Album { CoverArtUrl = "https://covers.example.test/release.png" });
        byte[] art = [137, 80, 78, 71, 13, 10, 26, 10];
        using var factory = ClientReleaseFactory(metadata, gateway, request => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(art) { Headers = { ContentType = new("image/png") } } });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(owner, new("deezer", "release-artist", "Release artist"));
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        var inbox = Assert.Single((await releases.ReadAsync(owner)).Entries);
        using var response = await client.GetAsync("/allstarr/v1/releases");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var release = Assert.Single(body.RootElement.GetProperty("releases").EnumerateArray());
        Assert.Equal(inbox.Id, release.GetProperty("releaseId").GetString());
        Assert.Equal(inbox.Title, release.GetProperty("title").GetString());
        Assert.Equal(inbox.Type, release.GetProperty("kind").GetString());
        Assert.Equal(inbox.ReleaseDate, release.GetProperty("releaseDate").GetString());
        Assert.Equal("ext-deezer-album-release-album", release.GetProperty("albumId").GetString());
        Assert.Equal("ext-deezer-artist-release-artist", release.GetProperty("artist").GetProperty("artistId").GetString());
        Assert.Equal("Release artist", release.GetProperty("artist").GetProperty("name").GetString());
        var followed = Assert.Single(body.RootElement.GetProperty("followedArtists").EnumerateArray());
        Assert.Equal("ext-deezer-artist-release-artist", followed.GetProperty("artistId").GetString());
        Assert.Equal("/Items/ext-deezer-artist-release-artist/Images/Primary?tag=ext-deezer-artist-release-artist-art-v3",
            followed.GetProperty("imageUrl").GetString());
        var path = release.GetProperty("coverUrl").GetString();
        Assert.Equal($"/allstarr/v1/releases/{inbox.Id}/cover", path);
        using var cover = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/png", cover.Content.Headers.ContentType?.MediaType);
        Assert.Equal(art, await cover.Content.ReadAsByteArrayAsync());
        Assert.True(cover.Headers.CacheControl?.Private);
        Assert.NotNull(cover.Headers.ETag);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(cover.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        using var other = await client.GetAsync("/allstarr/v1/releases");
        using var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
        Assert.Empty(otherBody.RootElement.GetProperty("releases").EnumerateArray());
        Assert.Empty(otherBody.RootElement.GetProperty("followedArtists").EnumerateArray());
        using var hidden = await client.GetAsync(path);
        await AssertClientApiError(hidden, 404, "not_found");
        metadata.Verify(service => service.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()), Times.Once);
        gateway.Verify(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("ext-deezer-artist-release-artist", false)]
    [InlineData("native-release-artist", true)]
    public async Task ClientApi_FollowAndUnfollowAreIdempotentAndPersonal(string artistId, bool library)
    {
        var metadata = ClientReleaseMetadata();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release-artist"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-release-artist", Name = "Release artist", ExternalProvider = "deezer", ExternalId = "release-artist" });
        using var factory = ClientReleaseFactory(metadata, gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var resolver = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var owner = (await resolver.ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var other = (await resolver.ResolveAsync(new("Jellyfin", "other")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(other, new("deezer", "release-artist", "Release artist"));
        var path = $"/allstarr/v1/artists/{artistId}/follow";
        foreach (var followed in new[] { true, true, false, false })
        {
            using var request = new HttpRequestMessage(followed ? HttpMethod.Put : HttpMethod.Delete, path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(followed, body.RootElement.GetProperty("followed").GetBoolean());
            Assert.Equal(artistId, body.RootElement.GetProperty("artist").GetProperty("artistId").GetString());
            Assert.Equal("Release artist", body.RootElement.GetProperty("artist").GetProperty("name").GetString());
            Assert.Equal(followed ? 1 : 0, (await releases.ListAsync(owner)).Count);
            if (followed && library) Assert.Equal(artistId, Assert.Single(await releases.ListAsync(owner)).LibraryArtistId);
            Assert.Single(await releases.ListAsync(other));
        }
        metadata.Verify(service => service.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
    }

    // A heart on an artist in any Jellyfin app follows it for new releases; removing the heart unfollows it.
    [Fact]
    public async Task Jellyfin_FavouritingAnArtistFollowsIt()
    {
        var metadata = ClientReleaseMetadata();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release-artist"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-release-artist", Name = "Release artist", ExternalProvider = "deezer", ExternalId = "release-artist" });
        using var factory = ClientReleaseFactory(metadata, gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();

        using (var favourite = await client.PostAsync("/UserFavoriteItems/ext-deezer-artist-release-artist", null))
            Assert.Equal(HttpStatusCode.OK, favourite.StatusCode);
        Assert.Equal(("deezer", "release-artist"), (await releases.ListAsync(owner)).Select(item => (item.ProviderId, item.ArtistId)).Single());
        using (var song = await client.PostAsync("/UserFavoriteItems/ext-deezer-song-release-song", null))
            Assert.Equal(HttpStatusCode.OK, song.StatusCode);
        Assert.Single(await releases.ListAsync(owner));
        using (var unfavourite = await client.DeleteAsync("/UserFavoriteItems/ext-deezer-artist-release-artist"))
            Assert.Equal(HttpStatusCode.OK, unfavourite.StatusCode);
        Assert.Empty(await releases.ListAsync(owner));
    }

    [Theory]
    [InlineData("unknown-artist")]
    [InlineData("ext-deezer-artist-missing")]
    [InlineData("ext-deezer-album-release-album")]
    public async Task ClientApi_FollowReturnsClearNotFoundForUnresolvableArtists(string artistId)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"/allstarr/v1/artists/{artistId}/follow");
            using var response = await client.SendAsync(request);
            await AssertClientApiError(response, 404, "artist_not_found");
        }
    }

    [Fact]
    public async Task ClientApi_FollowMapsPublicAppleArtistIdsToTheCatalogProvider()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "apple-artist"))
            .ReturnsAsync(new Artist { Id = "ext-apple-download-artist-apple-artist", Name = "Apple artist" });
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var followed = await client.PutAsync("/allstarr/v1/artists/ext-apple-download-artist-apple-artist/follow", null);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var artist = Assert.Single(await factory.Services.GetRequiredService<NewReleasesService>().ListAsync(owner));
        Assert.Equal("apple-musickit", artist.ProviderId);
        Assert.Equal("apple-artist", artist.ArtistId);
        using var inbox = await client.GetAsync("/allstarr/v1/releases");
        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        using var body = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync());
        Assert.Equal("ext-apple-download-artist-apple-artist", Assert.Single(body.RootElement.GetProperty("followedArtists").EnumerateArray()).GetProperty("artistId").GetString());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_LibraryFollowKeepsNativeIdentityAndPictureEvenWithoutAServiceMatch(bool overlap)
    {
        var metadata = ClientReleaseMetadata();
        using var factory = ClientReleaseFactory(metadata, new(), library: request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/listener/Items/native-release-artist" => Json(200,
                """{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist","ProviderIds":null,"ImageTags":{"Primary":"native-picture"}}"""),
            "/Users/listener/Items" => Json(200, overlap
                ? """{"Items":[{"Name":"New record","Type":"MusicAlbum"}]}"""
                : """{"Items":[{"Name":"Other record","Type":"MusicAlbum"}]}"""),
            _ => null
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        const string path = "/allstarr/v1/artists/native-release-artist/follow";
        using (var response = await client.PutAsync(path, null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertNativeReleaseArtist(body.RootElement.GetProperty("artist"));
        }
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        using (var response = await client.GetAsync("/allstarr/v1/releases"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertNativeReleaseArtist(Assert.Single(body.RootElement.GetProperty("followedArtists").EnumerateArray()));
            Assert.Equal(overlap ? 1 : 0, body.RootElement.GetProperty("releases").GetArrayLength());
            if (overlap) AssertNativeReleaseArtist(Assert.Single(body.RootElement.GetProperty("releases").EnumerateArray()).GetProperty("artist"));
        }
        using (var response = await client.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertNativeReleaseArtist(body.RootElement.GetProperty("artist"));
        }
        Assert.Empty(await releases.ListAsync(owner));
        if (!overlap)
            metadata.Verify(service => service.GetArtistAlbumsAsync(It.Is<ProviderExecutionContext>(context => context.OperationId == "releases-artist-albums"),
                It.IsAny<ProviderArtistItemsRequest>()), Times.Never);
    }

    private static void AssertNativeReleaseArtist(JsonElement artist)
    {
        Assert.Equal("native-release-artist", artist.GetProperty("artistId").GetString());
        Assert.Equal("Release artist", artist.GetProperty("name").GetString());
        Assert.Equal("/Items/native-release-artist/Images/Primary?tag=native-picture", artist.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task ClientApi_LibraryFollowKeepsAnUnconfirmedMusicBrainzIdentifierLibraryOnly()
    {
        var mbid = Guid.NewGuid().ToString();
        var metadata = ClientReleaseMetadata();
        metadata.Setup(service => service.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "release-artist"), "Release artist",
                    providerIds: new Dictionary<string, string> { ["MusicBrainzArtist"] = mbid })])));
        using var factory = ClientReleaseFactory(metadata, new(), library: request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/listener/Items/native-release-artist" => Json(200, JsonSerializer.Serialize(new
            {
                Id = "native-release-artist",
                Name = "Release artist",
                Type = "MusicArtist",
                ProviderIds = new { MusicBrainzArtist = mbid },
                ImageTags = new { Primary = "native-picture" }
            })),
            _ => null
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PutAsync("/allstarr/v1/artists/native-release-artist/follow", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertNativeReleaseArtist(body.RootElement.GetProperty("artist"));
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        var stored = Assert.Single(await releases.ListAsync(owner));
        Assert.Equal("native-release-artist", stored.ArtistId);
        Assert.Equal("jellyfin", stored.ProviderId);
        Assert.Equal("native-release-artist", stored.LibraryArtistId);
        Assert.NotNull(stored.ResolutionReason);
        Assert.Equal("library-only", Assert.Single((await releases.ReadAsync(owner)).Coverage).Status);
        metadata.Verify(service => service.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
    }

    [Fact]
    public async Task ClientApi_LibraryFollowDoesNotRemoveADirectFollowOfTheSameServiceArtist()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release-artist"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-release-artist", Name = "Release artist" });
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), gateway, library: request => request.RequestUri!.AbsolutePath == "/Users/listener/Items"
            ? Json(200, """{"Items":[{"Name":"New record","Type":"MusicAlbum"}]}""") : null);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        using (var response = await client.PutAsync("/allstarr/v1/artists/ext-deezer-artist-release-artist/follow", null))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await client.PutAsync("/allstarr/v1/artists/native-release-artist/follow", null))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await releases.ListAsync(owner)).Count);
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        Assert.Single((await releases.ReadAsync(owner)).Entries);
        using (var response = await client.DeleteAsync("/allstarr/v1/artists/native-release-artist/follow"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var direct = Assert.Single(await releases.ListAsync(owner));
        Assert.Equal("release-artist", direct.ArtistId);
        Assert.Null(direct.LibraryArtistId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_ReleasesRepairLegacyLibraryFollowsBackedByNativeHearts(bool overlap)
    {
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), new(), library: request => request.RequestUri!.AbsolutePath switch
        {
            "/Items" => Json(200, """{"Items":[{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist"}]}"""),
            "/UserFavoriteItems/native-release-artist" => Json(200, "{}"),
            "/Users/listener/Items/native-release-artist" => Json(200,
                """{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist","ImageTags":{"Primary":"native-picture"}}"""),
            "/Users/listener/Items" => Json(200, request.RequestUri.Query.Contains("IsFavorite", StringComparison.Ordinal)
                ? """{"Items":[{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist"}]}"""
                : overlap ? """{"Items":[{"Name":"New record","Type":"MusicAlbum"}]}""" : """{"Items":[]}"""),
            _ => null
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        using (var heart = await client.PostAsync("/UserFavoriteItems/native-release-artist", null))
            Assert.True(heart.StatusCode == HttpStatusCode.OK, await heart.Content.ReadAsStringAsync());
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner && item.ResourceKind == "release-follows")
                .OrderByDescending(item => item.SnapshotVersion).FirstAsync();
            snapshot.Id = Guid.CreateVersion7();
            snapshot.SnapshotVersion++;
            snapshot.PayloadJson = """[{"ProviderId":"deezer","ArtistId":"wrong-artist","DisplayName":"Release artist"},{"ProviderId":"deezer","ArtistId":"direct-artist","DisplayName":"Direct artist"}]""";
            snapshot.PayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot.PayloadJson))).ToLowerInvariant();
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }
        using (var response = await client.GetAsync("/allstarr/v1/releases"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var follows = body.RootElement.GetProperty("followedArtists").EnumerateArray().ToArray();
            Assert.Equal(2, follows.Length);
            AssertNativeReleaseArtist(follows.Single(item => item.GetProperty("name").GetString() == "Release artist"));
            Assert.Contains(follows, item => item.GetProperty("artistId").GetString() == "ext-deezer-artist-direct-artist");
        }
        var stored = await releases.ListAsync(owner);
        Assert.DoesNotContain(stored, item => item.ArtistId == "wrong-artist");
        Assert.Contains(stored, item => item.ArtistId == (overlap ? "release-artist" : "native-release-artist"));
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        Assert.DoesNotContain((await releases.ReadAsync(owner)).Coverage, item => item.Artist.ArtistId == "wrong-artist");
        using var second = await client.GetAsync("/allstarr/v1/releases");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, (await releases.ListAsync(owner)).Count);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task Releases_repair_legacy_follows_without_hearts_on_client_and_admin_reads(bool overlap, bool admin, bool accessible)
    {
        var searches = 0;
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), new(), library: request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/listener/Items/native-release-artist" => accessible ? Json(200,
                """{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist","ImageTags":{"Primary":"native-picture"}}""") : Json(403, "{}"),
            "/Users/listener/Items" when request.RequestUri.Query.Contains("SearchTerm", StringComparison.Ordinal) => Search(),
            "/Users/listener/Items" when request.RequestUri.Query.Contains("ArtistIds", StringComparison.Ordinal) => Json(200, overlap
                ? """{"Items":[{"Name":"New record","Type":"MusicAlbum"}]}""" : """{"Items":[]}"""),
            "/Users/listener/Items" => Json(200, """{"Items":[]}"""),
            _ => null
        });
        HttpResponseMessage Search()
        {
            searches++;
            return Json(200, """{"Items":[{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist"},{"Id":"tribute","Name":"Release artist tribute","Type":"MusicArtist"}]}""");
        }
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(owner, new("deezer", "wrong-artist", "Release artist"));
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.OwnerUserId == owner && item.ResourceKind == "release-follows");
            snapshot.Id = Guid.CreateVersion7();
            snapshot.SnapshotVersion++;
            snapshot.PayloadJson = """[{"ProviderId":"deezer","ArtistId":"wrong-artist","DisplayName":"  RELEASE ARTIST! "},{"ProviderId":"deezer","ArtistId":"unmatched","DisplayName":"Other artist"},{"ProviderId":"deezer","ArtistId":"direct","DisplayName":"Release artist","IsDirectFollow":true}]""";
            snapshot.PayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot.PayloadJson))).ToLowerInvariant();
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }
        if (admin)
        {
            var http = new DefaultHttpContext();
            http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
            {
                SessionId = "release-read",
                UserId = "listener",
                UserName = "Listener",
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                AllstarrUserId = owner,
                JellyfinAccessToken = "listener-token",
                IsAdministrator = false,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
            http.Request.Headers["X-Emby-Token"] = "listener-token";
            var controller = new NewReleasesController(releases, factory.Services.GetRequiredService<AdminProtocolExecutionContextFactory>(),
                reader: factory.Services.GetRequiredService<ReleaseReader>())
            { ControllerContext = new() { HttpContext = http } };
            Assert.IsType<OkObjectResult>(await controller.List(default));
            Assert.IsType<OkObjectResult>(await controller.Inbox(default));
        }
        using (var response = await client.GetAsync("/allstarr/v1/releases"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (accessible) AssertNativeReleaseArtist(body.RootElement.GetProperty("followedArtists").EnumerateArray()
                .Single(item => item.GetProperty("artistId").GetString() == "native-release-artist"));
            else Assert.DoesNotContain(body.RootElement.GetProperty("followedArtists").EnumerateArray(),
                item => item.GetProperty("artistId").GetString() == "native-release-artist");
        }
        var follows = await releases.ListAsync(owner);
        if (!accessible)
        {
            Assert.Contains(follows, item => item.ArtistId == "wrong-artist" && item.LibraryArtistId == null);
            return;
        }
        Assert.Equal(3, follows.Count);
        var native = Assert.Single(follows, item => item.LibraryArtistId != null);
        Assert.Equal(overlap ? "release-artist" : "native-release-artist", native.ArtistId);
        Assert.Equal("native-picture", native.LibraryImageTag);
        Assert.Contains(follows, item => item.ArtistId == "unmatched" && item.LibraryArtistId == null);
        Assert.Contains(follows, item => item.ArtistId == "direct" && item.IsDirectFollow == true && item.LibraryArtistId == null);
        Assert.Equal(2, searches);
        using var second = await client.GetAsync("/allstarr/v1/releases");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, searches);
        Assert.Equal(follows, await releases.ListAsync(owner));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Releases_admin_library_artwork_uses_only_the_accessible_native_artist(bool accessible)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        byte[] picture = [137, 80, 78, 71, 13, 10, 26, 10];
        var imageRequests = 0;
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), gateway, library: request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/listener/Items/native-release-artist" => accessible ? Json(200,
                """{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist","ImageTags":{"Primary":"native-picture"}}""") : Json(403, "{}"),
            "/Items/native-release-artist/Images/Primary" => Image(request),
            _ => null
        });
        HttpResponseMessage Image(HttpRequestMessage request)
        {
            imageRequests++;
            Assert.Contains("tag=native-picture", request.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Contains("listener-token", request.Headers.ToString(), StringComparison.Ordinal);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) { Headers = { ContentType = new("image/png") } } };
        }
        using var client = CreateClientApiClient(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "release-artwork",
            UserId = "listener",
            UserName = "Listener",
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            AllstarrUserId = owner,
            JellyfinAccessToken = "listener-token",
            IsAdministrator = false,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var controller = new RecapArtworkController(factory.Services.GetRequiredService<AdminProtocolExecutionContextFactory>(),
            factory.Services.GetRequiredService<RecapArtworkReader>(), factory.Services.GetRequiredService<JellyfinProxyService>())
        { ControllerContext = new() { HttpContext = http } };
        var result = await controller.Get("Release artist", libraryArtistId: "native-release-artist");
        if (accessible) Assert.Equal(picture, Assert.IsType<FileContentResult>(result).FileContents);
        else Assert.IsType<NoContentResult>(result);
        Assert.Equal(accessible ? 1 : 0, imageRequests);
        gateway.VerifyNoOtherCalls();
    }

    private static Mock<IProviderMetadataCapability> ClientReleaseMetadata()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(service => service.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderMetadataSearchRequest request) => ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "near-match"), "Release artist tribute"),
                    new(new("deezer", ProviderResourceKind.Artist, "release-artist"), request.Query == "Release artist" ? "Release artist" : "Other artist")])));
        metadata.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderArtistItemsRequest request) => ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer",
                request.Id.Value == "near-match" ? [] :
                [new(new("deezer", ProviderResourceKind.Album, "release-album"), "New record", [new("Release artist", new("deezer", ProviderResourceKind.Artist, "release-artist"))],
                    releaseDate: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), releaseType: "album", isExplicit: false)])));
        metadata.Setup(service => service.GetArtistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [])));
        return metadata;
    }

    private static ProtocolFactory ClientReleaseFactory(Mock<IProviderMetadataCapability> metadata, Mock<IProtocolProviderGateway> gateway,
        Func<HttpRequestMessage, HttpResponseMessage>? artwork = null,
        Func<HttpRequestMessage, HttpResponseMessage?>? library = null, Action<IServiceCollection>? configure = null) => ClientDetailsFactory(request =>
    {
        if (request.RequestUri!.Host == "covers.example.test") return artwork!(request);
        if (library?.Invoke(request) is { } response) return response;
        return request.RequestUri.AbsolutePath switch
        {
            "/Users/Me" => Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal) ? """{"Id":"other"}""" : """{"Id":"listener","Policy":{"IsAdministrator":true}}"""),
            "/Users/listener/Items/native-release-artist" => Json(200, """{"Id":"native-release-artist","Name":"Release artist","Type":"MusicArtist"}"""),
            "/Users/listener/Items/unmatched-artist" => Json(200, """{"Id":"unmatched-artist","Name":"Unmatched artist","Type":"MusicArtist"}"""),
            _ => Json(404, "{}")
        };
    }, services =>
    {
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(service => service.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var provider = request.ProviderPriority.First();
                var descriptor = new ProviderDescriptor(provider, provider, "Catalog fixture", ProviderOrigin.BuiltIn, "1", "1",
                    [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getArtistAlbums"])], new());
                var context = new ProviderExecutionContext(request.Actor, provider, null, request.Policy, request.OperationId,
                    request.CorrelationId, request.Deadline, request.CancellationToken);
                return new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(0, descriptor, descriptor.Capabilities.Single(), metadata.Object, context, null)],
                    new(request.CorrelationId, request.Capability, provider, null, []));
            });
        services.RemoveAll<IProviderRouter>();
        services.AddSingleton(router.Object);
        configure?.Invoke(services);
    });
}
