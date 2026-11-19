using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
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
    public async Task ClientApiV2_AServiceArtistNamesTheLibraryArtistItIsOrCouldBe()
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid owner, CancellationToken _) => new BackendLibraryAccessContext(
                new(ProtocolKind.Jellyfin, "primary", "listener", new(owner, "jellyfin", "primary", "listener", "Listener", false),
                    "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default), new(true, ["music"])));
        var gateway = V2BatchGateway();
        gateway.Setup(item => item.GetStoredArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "9"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-9", Name = "LIBRARY ARTIST", ExternalProvider = "deezer", ExternalId = "9" });
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "9")).ReturnsAsync([
            new Album { Id = "ext-deezer-album-7", Title = "Owned album (Deluxe)", Artist = "Library artist", ArtistId = "ext-deezer-artist-9" }]);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 0, 0, 20, null))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult());
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Artists" => Json(200, """{"Items":[{"Id":"artist1","Name":"Library artist"}]}"""),
            "/Users/listener/Items/artist1" => Json(200, """{"Id":"artist1","Type":"MusicArtist","Name":"Library artist","ImageTags":{"Primary":"face"}}"""),
            _ => Json(200, """{"Items":[]}""")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var path = $"/allstarr/v2/artists/{V2Ids.Artist("ext-deezer-artist-9")}";
        async Task<JsonNode> ReadAsync(string url)
        {
            using var response = await client.GetAsync(url);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            return JsonNode.Parse(text)!;
        }

        // Only the name agrees, which never joins two artists: the page can ask.
        var unproven = await ReadAsync(path + "?include=albums");
        Assert.Null(unproven["libraryArtist"]);
        var could = Assert.Single(unproven["couldBe"]!.AsArray())!;
        Assert.Equal(V2Ids.Artist("artist1"), could["id"]!.GetValue<string>());
        Assert.Equal("artist1", could["libraryId"]!.GetValue<string>());
        Assert.Equal("Library artist", could["name"]!.GetValue<string>());
        Assert.NotNull(could["artwork"]);
        Assert.Equal("Same name. No matched song or shared album yet.", could["reason"]!.GetValue<string>());

        // The library holds one of this artist's albums, so the album list proves they are one artist.
        var principal = await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "listener"));
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.LibraryTracks.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = principal!.UserId,
                BackendLibraryId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendItemId = "song1",
                FilePath = "/music/song.flac",
                Title = "Owned song",
                Artist = "Library artist",
                AlbumArtist = "Library artist",
                Album = "Owned album",
                IndexedAt = DateTimeOffset.UtcNow,
                SourceModifiedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var proven = await ReadAsync(path + "?include=albums");
        Assert.Null(proven["couldBe"]);
        Assert.Equal(V2Ids.Artist("artist1"), proven["libraryArtist"]!["id"]!.GetValue<string>());
        Assert.Equal("albums", proven["libraryArtist"]!["by"]!.GetValue<string>());
        // Later reads need no album list, and the library artist's links say why.
        Assert.Equal("albums", (await ReadAsync(path))["libraryArtist"]!["by"]!.GetValue<string>());
        var link = Assert.Single((await ReadAsync("/allstarr/v2/artists/artist1/links"))["links"]!.AsArray())!;
        Assert.Equal(V2Ids.Artist("ext-deezer-artist-9"), link["id"]!.GetValue<string>());
        Assert.Equal("albums", link["by"]!.GetValue<string>());
        Assert.Equal("Owned album", link["album"]!.GetValue<string>());

        // A person's "not the same" outranks the album, and the page stops suggesting it.
        using var refuse = await client.PutAsJsonAsync("/allstarr/v2/artists/artist1/links/ext-deezer-artist-9", new { same = false });
        refuse.EnsureSuccessStatusCode();
        var apart = await ReadAsync(path + "?include=albums");
        Assert.Null(apart["libraryArtist"]);
        Assert.Null(apart["couldBe"]);
    }

    [Fact]
    public async Task ClientApiV2_ArtistLinksSaveAListenersAnswerAndSayWhyEachLinkStands()
    {
        var fixes = new List<LibraryArtistFix> { new("deezer", "namesake", false, true) };
        var links = new Mock<ILibraryArtistLinks>();
        links.Setup(item => item.ReadPartsAsync(It.IsAny<Guid>(), "Say Now", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ((IReadOnlyList<LibraryArtistLink>)[new("deezer", "1")], (IReadOnlyList<LibraryArtistFix>)fixes.ToArray()));
        links.Setup(item => item.ReadProofAsync(It.IsAny<Guid>(), "Say Now", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new LibraryArtistProof([new("deezer", "1")], [new("qobuz", "7", "Shared album")], fixes.ToArray()));
        // Candidates already tied or answered for are not offered again, whichever of a service's sources named them.
        links.Setup(item => item.ReadCandidatesAsync(It.IsAny<Guid>(), "Say Now", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new("tidal", "9", "Say Now", 0, [], true), new("spotiflac-deezer", "1", "Say Now", 0, [], true),
                new("deezer", "namesake", "Say Now", 0, [], true), new("deezer", "second account", "Say Now", 0, [], true),
                new("tidal", "10", "Say Now", 0, [], true), new("tidal", "11", "Say Now", 0, [], true)]);
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
            var byAlbum = shown.Single(link => link.GetProperty("by").GetString() == "albums");
            Assert.Equal(V2Ids.Artist("ext-qobuz-artist-7"), byAlbum.GetProperty("id").GetString());
            Assert.Equal("Shared album", byAlbum.GetProperty("album").GetString());
            // Deezer already has its artist, so its other accounts are not offered; an untied service offers two at most.
            var offered = json.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.Equal(["ext-tidal-artist-9", "ext-tidal-artist-10"], offered.Select(item => item.GetProperty("aliases")[0].GetString()));
            var candidate = offered[0];
            Assert.Equal(V2Ids.Artist("ext-tidal-artist-9"), candidate.GetProperty("id").GetString());
            Assert.Equal(["ext-tidal-artist-9"], candidate.GetProperty("aliases").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("Say Now", candidate.GetProperty("name").GetString());
            Assert.Equal("Same name. No matched song or shared album yet.", candidate.GetProperty("reason").GetString());

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
