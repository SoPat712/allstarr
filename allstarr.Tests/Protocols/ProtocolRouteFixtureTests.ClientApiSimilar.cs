using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_SimilarAlbumsPreferTheLibraryAndOpenServiceAlbumsThroughAllstarr()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed-album"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-seed-album", Title = "Seed album", Artist = "Seed Artist", ArtistId = "ext-deezer-artist-seed" });
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([
                new Artist { Name = "Library Friend", ExternalProvider = "deezer", ExternalId = "friend" },
                new Artist { Name = "Service Friend", ExternalProvider = "deezer", ExternalId = "other" },
                new Artist { Name = "Seed Artist", ExternalProvider = "deezer", ExternalId = "seed" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other", It.IsAny<int>()))
            .ReturnsAsync([
                new Album { Id = "ext-deezer-album-other-single", Title = "A single", Artist = "Service Friend", ReleaseType = "single", ExternalProvider = "deezer", ExternalId = "other-single" },
                new Album { Id = "ext-deezer-album-other-album", Title = "Other album", Artist = "Service Friend", ArtistId = "ext-deezer-artist-other", Year = 2020, SongCount = 10, ExternalProvider = "deezer", ExternalId = "other-album" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-seed-album", Title = "Seed album", Artist = "Seed Artist", ExternalProvider = "deezer", ExternalId = "seed-album" }]);
        using var factory = ClientDetailsFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (path == "/Artists/AlbumArtists" && query.Contains("searchTerm=Library Friend", StringComparison.Ordinal))
                return Json(200, """{"Items":[{"Id":"lib-artist","Name":"Library Friend"}]}""");
            if (path == "/Users/listener/Items" && query.Contains("albumArtistIds=lib-artist", StringComparison.Ordinal))
                return Json(200, """{"Items":[{"Id":"lib-album","Type":"MusicAlbum","Name":"Owned album","AlbumArtist":"Library Friend","ProductionYear":2019,"ChildCount":9,"ImageTags":{"Primary":"owned-art"}}]}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-album-seed-album/similar?limit=12");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("deezer", body.RootElement.GetProperty("basedOn").GetString());
        var albums = body.RootElement.GetProperty("albums").EnumerateArray().ToArray();
        Assert.Equal(["lib-album", "ext-deezer-album-other-album"], albums.Select(album => album.GetProperty("id").GetString()));
        Assert.Equal("library", albums[0].GetProperty("source").GetString());
        Assert.False(albums[0].GetProperty("isExternal").GetBoolean());
        Assert.Equal(2019, albums[0].GetProperty("year").GetInt32());
        Assert.Equal(9, albums[0].GetProperty("songCount").GetInt32());
        Assert.Equal("deezer", albums[1].GetProperty("source").GetString());
        Assert.True(albums[1].GetProperty("isExternal").GetBoolean());
        Assert.Equal("Other album", albums[1].GetProperty("title").GetString());
        Assert.Equal("ext-deezer-artist-other", albums[1].GetProperty("artistId").GetString());
        Assert.Equal("deezer", albums[1].GetProperty("provider").GetProperty("id").GetString());
        Assert.StartsWith("/Items/ext-deezer-album-other-album/Images/Primary", albums[1].GetProperty("coverUrl").GetString());
        gateway.Verify(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other", 24), Times.Once);
        gateway.Verify(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "friend", It.IsAny<int>()), Times.Never);

        using var song = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-mix-song/similar");
        Assert.Equal(HttpStatusCode.BadRequest, song.StatusCode);
    }

    // Jellyfin's own Similar route gives albums the same picks as the client API, as ordinary album items.
    [Theory]
    [InlineData("/Items/ext-deezer-album-seed-album/Similar?Limit=11")]
    [InlineData("/Albums/ext-deezer-album-seed-album/Similar?Limit=11")]
    public async Task Jellyfin_SimilarForAnAlbumReturnsTheSameAlbumsAsJellyfinItems(string url)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed-album"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-seed-album", Title = "Seed album", Artist = "Seed Artist", ArtistId = "ext-deezer-artist-seed" });
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([
                new Artist { Name = "Library Friend", ExternalProvider = "deezer", ExternalId = "friend" },
                new Artist { Name = "Service Friend", ExternalProvider = "deezer", ExternalId = "other" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other", It.IsAny<int>()))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-other-album", Title = "Other album", Artist = "Service Friend", ArtistId = "ext-deezer-artist-other", Year = 2020, SongCount = 10, ExternalProvider = "deezer", ExternalId = "other-album" }]);
        using var factory = ClientDetailsFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (path == "/Users/listener/Items" && query.Contains("includeItemTypes=MusicAlbum", StringComparison.Ordinal))
                return Json(200, """{"Items":[{"Id":"lib-album","Type":"MusicAlbum","Name":"Owned album","AlbumArtist":"Library Friend","AlbumArtists":[{"Id":"lib-artist","Name":"Library Friend"}],"ProductionYear":2019,"ChildCount":9,"ImageTags":{"Primary":"owned-art"}}]}""");
            if (path == "/Artists/AlbumArtists" && query.Contains("searchTerm=Library Friend", StringComparison.Ordinal))
                return Json(200, """{"Items":[{"Id":"lib-artist","Name":"Library Friend"}]}""");
            if (path == "/Users/listener/Items" && (query.Contains("albumArtistIds=lib-artist", StringComparison.Ordinal) || query.Contains("Ids=lib-album", StringComparison.Ordinal)))
                return Json(200, """{"Items":[{"Id":"lib-album","Type":"MusicAlbum","Name":"Owned album","AlbumArtist":"Library Friend","ProductionYear":2019,"ChildCount":9,"ImageTags":{"Primary":"owned-art"}}]}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("deezer", body.RootElement.GetProperty("AllstarrBasedOn").GetString());
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(["lib-album", "ext-deezer-album-other-album"], items.Select(item => item.GetProperty("Id").GetString()));
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal("owned-art", items[0].GetProperty("ImageTags").GetProperty("Primary").GetString());
        Assert.Equal("library", items[0].GetProperty("AllstarrSource").GetString());
        Assert.Equal("MusicAlbum", items[1].GetProperty("Type").GetString());
        // Service albums carry the same label as everywhere else in Jellyfin clients.
        Assert.Equal("Other album [A]", items[1].GetProperty("Name").GetString());
        Assert.Equal("deezer", items[1].GetProperty("AllstarrSource").GetString());
        Assert.Equal("deezer", items[1].GetProperty("AllstarrProvider").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Jellyfin_SimilarForALibrarySongStillAsksTheMediaServer()
    {
        var asked = new List<string>();
        using var factory = ClientDetailsFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            asked.Add(path);
            if (path == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (path == "/Users/listener/Items/lib-song") return Json(200, """{"Id":"lib-song","Type":"Audio","Name":"Library song"}""");
            if (path == "/Items" && request.RequestUri.Query.Contains("ids=lib-song", StringComparison.OrdinalIgnoreCase))
                return Json(200, """{"Items":[{"Id":"lib-song","Type":"Audio","Name":"Library song"}],"TotalRecordCount":1}""");
            if (path == "/Items/lib-song/Similar") return Json(200, """{"Items":[{"Id":"native-similar","Type":"Audio","Name":"Native pick"}],"TotalRecordCount":1}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, _ => { });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/Items/lib-song/Similar?Limit=5");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.Contains("native-similar", text);
        Assert.DoesNotContain("AllstarrBasedOn", text);
        Assert.Contains("/Items/lib-song/Similar", asked);
    }

    [Fact]
    public async Task ClientApi_SimilarAlbumsForALibraryAlbumFindTheArtistOnDeezer()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["apple-musickit"]);
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Artist Seed album", 0, 5, 0, "deezer"))
            .ReturnsAsync(new SearchResult { Albums = [new Album { Title = "Seed album", Artist = "Seed Artist", ArtistId = "ext-deezer-artist-seed", ExternalProvider = "deezer" }] });
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Artist", 0, 0, 10, "deezer"))
            .ReturnsAsync(new SearchResult
            {
                Artists = [new Artist { Name = "Seed Artist", ExternalProvider = "deezer", ExternalId = "namesake" },
                new Artist { Name = "Seed Artist", ExternalProvider = "deezer", ExternalId = "seed" }]
            });
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([new Artist { Name = "Service Friend", ExternalProvider = "deezer", ExternalId = "other" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other", It.IsAny<int>()))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-other-album", Title = "Other album", Artist = "Service Friend", ExternalProvider = "deezer", ExternalId = "other-album" }]);
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Users/listener/Items/library-seed" => Json(200, """{"Id":"library-seed","Type":"MusicAlbum","Name":"Seed album","AlbumArtist":"Seed Artist"}"""),
            _ => Json(200, """{"Items":[],"TotalRecordCount":0}""")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/items/library-seed/similar");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("deezer", body.RootElement.GetProperty("basedOn").GetString());
        Assert.Equal("ext-deezer-album-other-album", Assert.Single(body.RootElement.GetProperty("albums").EnumerateArray()).GetProperty("id").GetString());
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "namesake", It.IsAny<int>()), Times.Never);
        gateway.Verify(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other", 24), Times.Once);
    }

    [Fact]
    public async Task ClientApi_SimilarAlbumsFallBackToArtistSearchWhenAlbumSearchFails()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Artist Seed album", 0, 5, 0, "deezer"))
            .ThrowsAsync(new HttpRequestException("album search failed"));
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Artist", 0, 0, 10, "deezer"))
            .ReturnsAsync(new SearchResult { Artists = [new Artist { Name = "Seed Artist", ExternalProvider = "deezer", ExternalId = "seed" }] });
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([new Artist { Name = "Service Friend", ExternalProvider = "deezer", ExternalId = "other" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other", It.IsAny<int>()))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-other-album", Title = "Other album", Artist = "Service Friend", ExternalProvider = "deezer", ExternalId = "other-album" }]);
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Users/listener/Items/library-fallback" => Json(200, """{"Id":"library-fallback","Type":"MusicAlbum","Name":"Seed album","AlbumArtist":"Seed Artist"}"""),
            _ => Json(200, """{"Items":[],"TotalRecordCount":0}""")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/items/library-fallback/similar");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("ext-deezer-album-other-album", Assert.Single(body.RootElement.GetProperty("albums").EnumerateArray()).GetProperty("id").GetString());
        gateway.Verify(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Artist", 0, 0, 10, "deezer"), Times.Once);
    }

    [Fact]
    public async Task ClientApi_SimilarAlbumsCacheIsPersonalAndFollowsListenerSettings()
    {
        var clean = false;
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "cache-seed"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-cache-seed", Title = "Seed", Artist = "Seed Artist", ArtistId = "ext-deezer-artist-cache-seed" });
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "cache-seed", It.IsAny<int>()))
            .ReturnsAsync([new Artist { Name = "Service Friend", ExternalProvider = "deezer", ExternalId = "cache-friend" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "cache-friend", It.IsAny<int>()))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-cache-other", Title = "Other", Artist = "Service Friend", ExternalProvider = "deezer", ExternalId = "cache-other" }]);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { UserId = user, Preferences = new(clean ? "CleanOnly" : "All") });
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal) ? """{"Id":"other"}""" : """{"Id":"listener"}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        async Task<HttpStatusCode> Read()
        {
            using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-album-cache-seed/similar");
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Read());
        Assert.Equal(HttpStatusCode.OK, await Read());
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "cache-seed", It.IsAny<int>()), Times.Once);
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        Assert.Equal(HttpStatusCode.OK, await Read());
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "cache-seed", It.IsAny<int>()), Times.Exactly(2));
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        clean = true;
        Assert.Equal(HttpStatusCode.OK, await Read());
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "cache-seed", It.IsAny<int>()), Times.Exactly(3));
    }

    [Fact]
    public void SimilarAlbumsCacheKeyFollowsTheListenerAndTheSettingsThatShapeTheAnswer()
    {
        var owner = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        var other = Guid.Parse("028f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        string[] available = ["deezer", "qobuz"];
        var open = SimilarPolicy(false, ["qobuz", "deezer"], ProviderUseMode.Automatic);
        var clean = SimilarPolicy(true, ["qobuz", "deezer"], ProviderUseMode.Automatic);
        var reordered = SimilarPolicy(false, ["deezer", "qobuz"], ProviderUseMode.Automatic);
        var disabled = SimilarPolicy(false, ["qobuz", "deezer"], ProviderUseMode.Off);
        var lyrics = open with { ProviderOrders = open.ProviderOrders.SetItem(ProviderCapabilityKind.Lyrics, ["genius"]) };
        string Key(EffectiveProviderPolicySnapshot policy, Guid user, int limit = 12) =>
            SimilarAlbumsReader.CacheKey(user, "primary", "album", limit, policy, available);

        Assert.NotEqual(Key(open, owner), Key(open, other));
        Assert.NotEqual(Key(open, owner), Key(clean, owner));
        Assert.NotEqual(Key(open, owner), Key(reordered, owner));
        Assert.NotEqual(Key(open, owner), Key(disabled, owner));
        Assert.NotEqual(Key(open, owner), Key(open, owner, 1));
        Assert.Equal(Key(open, owner), Key(lyrics, owner));
        Assert.Equal(Key(open, owner), SimilarAlbumsReader.CacheKey(owner, "primary", "album", 12, open, available));
    }

    [Fact]
    public void SimilarAlbumsCacheEvictsTheOldestEntriesPastTheCap()
    {
        var prefix = "cap-" + Guid.NewGuid().ToString("N") + "-";
        var expired = prefix + "expired";
        // Tests running alongside share this cache, so this test overfills it with its own entries. They are older
        // than other tests' fresh entries, so the trim takes these first and keys[0] goes whatever else is cached.
        var keys = Enumerable.Range(0, SimilarAlbumsReader.MaxCacheEntries + 1).Select(index => prefix + index.ToString("D4")).ToArray();
        try
        {
            SimilarAlbumsReader.PutCacheForTests(expired, DateTimeOffset.UnixEpoch);
            var start = DateTimeOffset.UtcNow.AddSeconds(-90);
            for (var index = 0; index < keys.Length; index++)
                SimilarAlbumsReader.PutCacheForTests(keys[index], start.AddMilliseconds(index));
            SimilarAlbumsReader.TrimCacheForTests();
            Assert.False(SimilarAlbumsReader.ContainsCacheForTests(expired));
            Assert.False(SimilarAlbumsReader.ContainsCacheForTests(keys[0]));
            Assert.True(SimilarAlbumsReader.ContainsCacheForTests(keys[^1]));
        }
        finally
        {
            SimilarAlbumsReader.RemoveCacheForTests(expired);
            foreach (var key in keys) SimilarAlbumsReader.RemoveCacheForTests(key);
        }
    }

    private static EffectiveProviderPolicySnapshot SimilarPolicy(bool clean, string[] metadata, ProviderUseMode deezer) => new(
        ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Metadata, metadata.ToImmutableArray()),
        ImmutableHashSet<string>.Empty, "Lossless", 0.07)
    {
        Preferences = new(clean ? "CleanOnly" : "All"),
        ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("deezer", deezer)
    };
}
