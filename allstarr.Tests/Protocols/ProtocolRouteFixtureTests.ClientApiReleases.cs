using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
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
            Assert.Single(await releases.ListAsync(other));
        }
        if (library)
            metadata.Verify(service => service.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderMetadataSearchRequest>(request => request.Query == "Release artist")), Times.AtLeastOnce);
        else
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
    [InlineData("unmatched-artist")]
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

    private static Mock<IProviderMetadataCapability> ClientReleaseMetadata()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.Setup(service => service.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync((ProviderExecutionContext _, ProviderMetadataSearchRequest request) => ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "near-match"), "Release artist tribute"),
                    new(new("deezer", ProviderResourceKind.Artist, "release-artist"), request.Query == "Release artist" ? "Release artist" : "Other artist")])));
        metadata.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Album, "release-album"), "New record", [new("Release artist", new("deezer", ProviderResourceKind.Artist, "release-artist"))],
                    releaseDate: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), releaseType: "album", isExplicit: false)])));
        return metadata;
    }

    private static ProtocolFactory ClientReleaseFactory(Mock<IProviderMetadataCapability> metadata, Mock<IProtocolProviderGateway> gateway,
        Func<HttpRequestMessage, HttpResponseMessage>? artwork = null) => ClientDetailsFactory(request =>
    {
        if (request.RequestUri!.Host == "covers.example.test") return artwork!(request);
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
    });
}
