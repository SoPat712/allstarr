using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_ArtistLinksAdministratorCanCreateAndDeleteAHouseholdAnswer()
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid owner, CancellationToken _) => new BackendLibraryAccessContext(
                new(ProtocolKind.Jellyfin, "primary", "listener", new(owner, "jellyfin", "primary", "listener", "Listener", true),
                    "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default), new(true, [])));
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener","Policy":{"IsAdministrator":true}}"""),
            "/Users/listener/Items/artist1" => Json(200, """{"Id":"artist1","Type":"MusicArtist","Name":"Say Now"}"""),
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        const string path = "/allstarr/v2/artists/artist1/links/ext-deezer-artist-1";
        using var put = await client.PutAsJsonAsync(path, new { same = true });
        put.EnsureSuccessStatusCode();
        using var saved = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.Equal("household", Assert.Single(saved.RootElement.GetProperty("links").EnumerateArray()).GetProperty("scope").GetString());
        using var delete = await client.DeleteAsync(path);
        delete.EnsureSuccessStatusCode();
        using var cleared = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        Assert.Empty(cleared.RootElement.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task ClientApiV2_ArtistLinksSaveAListenersAnswerAndSayWhyEachLinkStands()
    {
        var fixes = new List<LibraryArtistFix> { new("deezer", "namesake", false, true) };
        var links = new Mock<ILibraryArtistLinks>();
        links.Setup(item => item.ReadPartsAsync(It.IsAny<Guid>(), "Say Now", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ((IReadOnlyList<LibraryArtistLink>)[new("deezer", "1")], (IReadOnlyList<LibraryArtistFix>)fixes.ToArray()));
        links.Setup(item => item.SetFixAsync(It.IsAny<Guid>(), "Say Now", "apple-musickit", "1680224047", true, It.IsAny<CancellationToken>()))
            .Callback(() => fixes.Add(new("apple-musickit", "1680224047", true, false))).ReturnsAsync(true);
        using var client = ArtistLinksClient(links.Object, out var factory);
        using (factory)
        {
            // Raw ids work as well as v2 ids, since apps still open artists by their older ids.
            using var response = await client.PutAsJsonAsync(
                $"/allstarr/v2/artists/{V2Ids.Artist("artist1")}/links/ext-apple-download-artist-1680224047", new { same = true });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var json = JsonDocument.Parse(body);
            var shown = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
            var songs = shown.Single(link => link.GetProperty("by").GetString() == "songs");
            Assert.Equal(V2Ids.Artist("ext-deezer-artist-1"), songs.GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, songs.GetProperty("scope").ValueKind);
            var fixedLink = shown.Single(link => link.GetProperty("by").GetString() == "fix");
            Assert.Equal("apple-musickit", fixedLink.GetProperty("source").GetString());
            Assert.Equal("you", fixedLink.GetProperty("scope").GetString());
            Assert.Equal(["ext-apple-download-artist-1680224047"], fixedLink.GetProperty("aliases").EnumerateArray().Select(value => value.GetString()));
            var notSame = Assert.Single(json.RootElement.GetProperty("notSame").EnumerateArray());
            Assert.Equal("household", notSame.GetProperty("scope").GetString());

            // A listener can't take back the household's answer, only override it for themselves.
            using var undo = await client.DeleteAsync("/allstarr/v2/artists/artist1/links/ext-deezer-artist-namesake");
            Assert.Equal(HttpStatusCode.Forbidden, undo.StatusCode);
            Assert.Equal("household_fix", JsonDocument.Parse(await undo.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
            links.Verify(item => item.SetFixAsync(It.IsAny<Guid>(), It.IsAny<string>(), "deezer", "namesake", null, It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Theory]
    [InlineData("artist1", "not-a-service-artist", HttpStatusCode.BadRequest)]
    [InlineData("artist1", "ext-deezer-album-1", HttpStatusCode.BadRequest)]
    [InlineData("ext-deezer-artist-2", "ext-deezer-artist-1", HttpStatusCode.NotFound)]
    [InlineData("album1", "ext-deezer-artist-1", HttpStatusCode.NotFound)]
    public async Task ClientApiV2_ArtistLinksNeedALibraryArtistAndAServiceArtist(string library, string service, HttpStatusCode expected)
    {
        var links = new Mock<ILibraryArtistLinks>(MockBehavior.Strict);
        using var client = ArtistLinksClient(links.Object, out var factory);
        using (factory)
        {
            using var response = await client.PutAsJsonAsync($"/allstarr/v2/artists/{library}/links/{service}", new { same = false });
            Assert.Equal(expected, response.StatusCode);
        }
    }

    private static HttpClient ArtistLinksClient(ILibraryArtistLinks links, out ProtocolFactory factory)
    {
        factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Users/listener/Items/artist1" => Json(200, """{"Id":"artist1","Type":"MusicArtist","Name":"Say Now"}"""),
            "/Users/listener/Items/album1" => Json(200, """{"Id":"album1","Type":"MusicAlbum","Name":"Brick By Brick"}"""),
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<ILibraryArtistLinks>();
            services.AddSingleton(links);
        });
        var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        return client;
    }
}
