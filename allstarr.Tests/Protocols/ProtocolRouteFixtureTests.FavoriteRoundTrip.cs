using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string HeartSong = "ext-deezer-song-heart";
    private const string HeartAlbum = "ext-deezer-album-heart";
    private const string HeartArtist = "ext-deezer-artist-heart";

    [Theory]
    [InlineData("song", "Finamp")]
    [InlineData("album", "Feishin")]
    [InlineData("artist", "Jellyfin Web")]
    public async Task FavoriteRoundTrip_JellyfinMutationReturnsUserItemData(string kind, string clientName)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = HeartGateway();
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emby-Authorization", $"MediaBrowser Client=\"{clientName}\", Device=\"fixture\", DeviceId=\"fixture\", Version=\"1\", Token=\"fixture\"");
        var id = $"ext-deezer-{kind}-heart";
        foreach (var favorite in new[] { true, false })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(favorite ? HttpMethod.Post : HttpMethod.Delete,
                $"/Users/user-1/FavoriteItems/{id}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertHeartUserData(document.RootElement, id, favorite);
            using var detail = await client.GetAsync($"/Users/user-1/Items/{id}");
            using var item = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            AssertHeartUserData(item.RootElement.GetProperty("UserData"), id, favorite);
        }
    }

    [Theory]
    [InlineData("/Users/user-1/Items/ext-deezer-song-heart", "song")]
    [InlineData("/Users/user-1/Items/ext-deezer-album-heart", "album")]
    [InlineData("/Users/user-1/Items/ext-deezer-artist-heart", "artist")]
    [InlineData("/Items?SearchTerm=heart&IncludeItemTypes=Audio,MusicAlbum,MusicArtist", "song")]
    [InlineData("/Items?AlbumIds=ext-deezer-album-heart&IncludeItemTypes=Audio", "song")]
    [InlineData("/Items?ArtistIds=ext-deezer-artist-heart&IncludeItemTypes=MusicAlbum", "album")]
    [InlineData("/Playlists/ext-deezer-playlist-heart/Items?UserId=user-1", "song")]
    public async Task FavoriteRoundTrip_JellyfinRefreshHydratesEveryItemShape(string path, string kind)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = HeartGateway();
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        var id = $"ext-deezer-{kind}-heart";
        foreach (var favorite in new[] { true, false })
        {
            using var mutation = await client.SendAsync(new HttpRequestMessage(favorite ? HttpMethod.Post : HttpMethod.Delete,
                $"/UserFavoriteItems/{id}?ApiKey=fixture"));
            Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
            using var response = await client.GetAsync(path + (path.Contains('?') ? "&" : "?") + "ApiKey=fixture");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var item = root.TryGetProperty("Items", out var items)
                ? Assert.Single(items.EnumerateArray(), row => row.GetProperty("Id").GetString() == id) : root;
            AssertHeartUserData(item.GetProperty("UserData"), id, favorite);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task FavoriteRoundTrip_JellyfinHydrationPreservesCachedArtworkAndExternalMetadata()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin",
            _ => throw new InvalidOperationException("Response hydration must not make backend requests"),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        var proxy = factory.Services.GetRequiredService<JellyfinProxyService>();
        var http = new DefaultHttpContext();
        http.Request.Path = "/Items";
        http.Request.QueryString = new("?Fields=MediaSources,MediaStreams,ParentId");
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin, "primary", "user-1",
            new AllstarrPrincipal(FavoriteViewer, "jellyfin", "primary", "user-1", "Viewer", false),
            "favorite-metadata", DateTimeOffset.UtcNow.AddMinutes(1), default);
        const string id = "ext-deezer-song-saved";
        const string hash = "L00000fQfQfQfQfQfQfQfQfQfQfQ";
        var artwork = new Mock<IMediaAssetResolver>(MockBehavior.Strict);
        artwork.Setup(value => value.GetCachedBlurHashAsync(new MediaAssetIdentity(
            FavoriteViewer, null, "deezer", "song", "saved", null, null, null))).ReturnsAsync(hash);
        var song = new Song
        {
            Id = id,
            ExternalProvider = "deezer",
            ExternalId = "saved",
            Title = "Saved song",
            Artist = "Saved artist",
            Album = "Saved album",
            AlbumId = "ext-deezer-album-saved",
            ReleaseDate = "2024-02-29",
            Duration = 100,
            Size = 12_000_000,
            Bitrate = 960_000,
            Codec = "flac",
            SampleRate = 96_000,
            BitDepth = 24,
            Channels = 2
        };
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http });
        const string native = """{ "Id" : "native", "Type":"Audio", "ServerId":"fixture-server", "UserData":{"IsFavorite":false,"future":"<keep>"}, "ImageBlurHashes":{"Primary":{"native-tag":"original"}} }""";
        var result = new ContentResult
        {
            ContentType = "application/json",
            Content = "{\"Items\":[" + native + "," + JsonSerializer.Serialize(builder.ConvertSongToJellyfinItem(song)) + "]}"
        };

        await new JellyfinItemResponseFilter(proxy, artwork.Object, new FavoriteViewReader(storage)).PrepareAsync(result, http);

        Assert.Contains(native, result.Content!, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Content!);
        var item = document.RootElement.GetProperty("Items")[1];
        AssertHeartUserData(item.GetProperty("UserData"), id, true);
        Assert.Equal(hash, item.GetProperty("ImageBlurHashes").GetProperty("Primary").GetProperty(id + "-art-v2").GetString());
        Assert.Equal("2024-02-29T00:00:00.0000000Z", item.GetProperty("PremiereDate").GetString());
        Assert.Equal(2024, item.GetProperty("ProductionYear").GetInt32());
        Assert.Equal(song.AlbumId, item.GetProperty("ParentId").GetString());
        var source = Assert.Single(item.GetProperty("MediaSources").EnumerateArray());
        Assert.Equal(12_000_000L, source.GetProperty("Size").GetInt64());
        Assert.Equal(960_000, source.GetProperty("Bitrate").GetInt32());
        var streams = source.GetProperty("MediaStreams").EnumerateArray().ToArray();
        Assert.Equal(["Audio", "Lyric"], streams.Select(value => value.GetProperty("Type").GetString()));
        var stream = streams[0];
        Assert.Equal(0, stream.GetProperty("Index").GetInt32());
        Assert.Equal(1, streams[1].GetProperty("Index").GetInt32());
        foreach (var key in new[] { "Size", "BitRate", "Codec", "SampleRate", "BitDepth", "Channels" })
            Assert.False(streams[1].TryGetProperty(key, out _));
        Assert.Equal("flac", stream.GetProperty("Codec").GetString());
        Assert.Equal(960_000, stream.GetProperty("BitRate").GetInt32());
        Assert.Equal(96_000, stream.GetProperty("SampleRate").GetInt32());
        Assert.Equal(24, stream.GetProperty("BitDepth").GetInt32());
        Assert.Equal(2, stream.GetProperty("Channels").GetInt32());
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        artwork.VerifyAll();
        artwork.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("&AlbumIds=ext-deezer-album-heart", true)]
    [InlineData("&ArtistIds=ext-deezer-artist-heart", true)]
    [InlineData("&AlbumArtistIds=ext-deezer-artist-heart", true)]
    [InlineData("&Genres=Jazz", true)]
    [InlineData("&GenreIds=genre-jazz", true)]
    [InlineData("&ParentId=ext-deezer-album-heart", true)]
    [InlineData("&ParentId=ext-deezer-artist-heart", true)]
    [InlineData("&Ids=ext-deezer-song-heart", true)]
    [InlineData("&AlbumIds=ext-deezer-album-other", false)]
    [InlineData("&ArtistIds=ext-deezer-artist-other", false)]
    [InlineData("&Genres=Rock", false)]
    [InlineData("&GenreIds=genre-rock", false)]
    [InlineData("&ArtistIds=ext-deezer-artist-heart&Genres=Rock", false)]
    [InlineData("&ParentId=unrelated-folder", false)]
    public async Task FavoriteRoundTrip_JellyfinScopesUseAcceptedMetadataWithoutProviderReads(string scope, bool matches)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = HeartGateway();
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        using var mutation = await client.PostAsync($"/UserFavoriteItems/{HeartSong}?ApiKey=fixture", null);
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        gateway.Invocations.Clear();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart"))
            .ThrowsAsync(new InvalidOperationException("Favorite reads must use saved metadata"));
        foreach (var flag in new[] { "IsFavorite=true", "Filters=IsFavorite" })
        {
            using var response = await client.GetAsync($"/Items?ApiKey=fixture&{flag}&IncludeItemTypes=Audio{scope}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = document.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(matches, items.Any(item => item.GetProperty("Id").GetString() == HeartSong));
            Assert.DoesNotContain(items, item => item.GetProperty("Id").GetString() == "ext-deezer-song-saved");
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json", "Feishin", "song", "getSong", "id=ext-deezer-song-heart")]
    [InlineData("json", "Tempo", "song", "getSong", "id=ext-deezer-song-heart")]
    [InlineData("xml", "Feishin", "song", "getSong", "id=ext-deezer-song-heart")]
    [InlineData("xml", "Tempo", "song", "getSong", "id=ext-deezer-song-heart")]
    [InlineData("json", "Feishin", "album", "getAlbum", "id=ext-deezer-album-heart")]
    [InlineData("json", "Tempo", "album", "getAlbum", "id=ext-deezer-album-heart")]
    [InlineData("xml", "Tempo", "album", "getAlbum", "id=ext-deezer-album-heart")]
    [InlineData("json", "Feishin", "artist", "getArtist", "id=ext-deezer-artist-heart")]
    [InlineData("json", "Tempo", "artist", "getArtist", "id=ext-deezer-artist-heart")]
    [InlineData("xml", "Tempo", "artist", "getArtist", "id=ext-deezer-artist-heart")]
    [InlineData("json", "Feishin", "song", "search3", "query=heart")]
    [InlineData("json", "Tempo", "song", "search3", "query=heart")]
    [InlineData("xml", "Tempo", "song", "search3", "query=heart")]
    [InlineData("json", "Feishin", "song", "getAlbum", "id=ext-deezer-album-heart")]
    [InlineData("xml", "Tempo", "song", "getAlbum", "id=ext-deezer-album-heart")]
    [InlineData("json", "Feishin", "song", "getAlbum", "id=ext-deezer-playlist-heart")]
    [InlineData("xml", "Tempo", "song", "getAlbum", "id=ext-deezer-playlist-heart")]
    [InlineData("json", "Feishin", "artist", "getArtists", "")]
    [InlineData("json", "Tempo", "artist", "getArtists", "")]
    [InlineData("xml", "Tempo", "artist", "getArtists", "")]
    public async Task FavoriteRoundTrip_SubsonicRefreshAndStarredAgree(string format, string clientName,
        string kind, string endpoint, string query)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        var gateway = HeartGateway();
        using var factory = HeartFactory("Subsonic", storage, gateway.Object);
        using var client = factory.CreateClient();
        var authentication = $"u=fixture&p=secret&v=1.13.0&c={clientName}&f={format}";
        var id = $"ext-deezer-{kind}-heart";
        var parameter = kind == "song" ? "id" : kind + "Id";
        foreach (var favorite in new[] { true, false })
        {
            using var mutation = await client.GetAsync($"/rest/{(favorite ? "star" : "unstar")}.view?{authentication}&{parameter}={id}");
            Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
            using var response = await client.GetAsync($"/rest/{endpoint}.view?{authentication}&{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var starred = ReadSubsonicHeart(body, format, id, required: endpoint != "getArtists" || favorite);
            Assert.Equal(favorite, starred != null);
            foreach (var list in new[] { "getStarred", "getStarred2" })
            {
                using var saved = await client.GetAsync($"/rest/{list}.view?{authentication}");
                Assert.Equal(starred, ReadSubsonicHeart(await saved.Content.ReadAsStringAsync(), format, id, required: favorite));
            }
        }
    }

    [Theory]
    [InlineData("Jellyfin", "json")]
    [InlineData("Subsonic", "json")]
    [InlineData("Subsonic", "xml")]
    public async Task FavoriteRoundTrip_ViewerStateIsIsolatedAndReadsNeverWrite(string backend, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, backend.ToLowerInvariant());
        using var factory = HeartFactory(backend, storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        using var mutation = backend == "Jellyfin"
            ? await client.PostAsync($"/UserFavoriteItems/{HeartSong}?ApiKey=fixture", null)
            : await client.GetAsync($"/rest/star.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}&id={HeartSong}");
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        await using (var db = storage.CreateDbContext())
        {
            var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").ToArrayAsync();
            foreach (var table in tables)
                foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
                {
                    var statement = $"CREATE TRIGGER \"readonly_{table}_{operation}\" BEFORE {operation} ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'Read request attempted a write'); END";
                    await db.Database.ExecuteSqlRawAsync(statement);
                }
        }
        foreach (var viewer in new[] { "foreign", "fixture", "foreign" })
        {
            var path = backend == "Jellyfin" ? $"/Items/{HeartSong}?ApiKey={viewer}"
                : $"/rest/getSong.view?u={viewer}&p=secret&v=1.13.0&c=Tempo&f={format}&id={HeartSong}";
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (backend == "Jellyfin")
            {
                using var document = JsonDocument.Parse(body);
                AssertHeartUserData(document.RootElement.GetProperty("UserData"), HeartSong, viewer == "fixture");
            }
            else Assert.Equal(viewer == "fixture", ReadSubsonicHeart(body, format, HeartSong, true) != null);
        }
    }

    [Theory]
    [InlineData("/Artists?IsFavorite=true")]
    [InlineData("/Artists/AlbumArtists?Filters=IsFavorite")]
    public async Task FavoriteRoundTrip_JellyfinFavoriteArtistIndexesIncludeSavedArtists(string path)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = HeartGateway();
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        using var mutation = await client.PostAsync($"/UserFavoriteItems/{HeartArtist}?ApiKey=fixture", null);
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        gateway.Invocations.Clear();
        using var response = await client.GetAsync(path + "&ApiKey=fixture&UserId=user-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray(), item => item.GetProperty("Id").GetString() == HeartArtist);
        AssertHeartUserData(item.GetProperty("UserData"), HeartArtist, true);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Jellyfin", "json")]
    [InlineData("Subsonic", "json")]
    [InlineData("Subsonic", "xml")]
    public async Task FavoriteRoundTrip_NativeReadsRemainByteExact(string backend, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, backend.ToLowerInvariant());
        var native = backend == "Jellyfin"
            ? """{ "Id" : "native", "Type":"Audio", "UserData" : {"IsFavorite":true,"future":"<keep>"}, "Name":"Native" }"""
            : format == "json"
                ? """{ "subsonic-response" : {"status":"ok","song":{"id":"native","starred":"2026-01-01T00:00:00Z","future":"<keep>"}} }"""
                : """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><song id='native' starred='2026-01-01T00:00:00Z' future='&lt;keep&gt;' /></subsonic-response>""";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory(backend, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.RequestUri.AbsolutePath == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok"}}""");
            if (backend == "Jellyfin" && request.RequestUri.AbsolutePath == "/Items")
                return Json(200, "{\"Items\":[" + native + "]}");
            return CatalogNative(format, native, native);
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin" ? "/Items/native?ApiKey=fixture"
            : $"/rest/getSong.view?u=fixture&p=secret&v=1.13.0&c=Tempo&f={format}&id=native");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(backend == "Jellyfin" ? JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(native)) : native,
            await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Jellyfin", "json", "Items")]
    [InlineData("Subsonic", "json", "getStarred2")]
    [InlineData("Subsonic", "xml", "getStarred2")]
    [InlineData("Subsonic", "json", "getArtists")]
    [InlineData("Subsonic", "xml", "getArtists")]
    public async Task FavoriteRoundTrip_MixedFavoriteNativeItemsStayByteExact(string backend, string format, string endpoint)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, backend.ToLowerInvariant());
        var native = backend == "Jellyfin"
            ? """{ "Id" : "native", "Type":"Audio", "UserData" : {"IsFavorite":true,"future":"<keep>"}, "Name":"Native" }"""
            : format == "json" ? """{ "id" : "native", "name":"Native", "starred":"2026-01-01T00:00:00Z", "future":"<keep>" }"""
            : """<artist id='native' name='Native' starred='2026-01-01T00:00:00Z' future='&lt;keep&gt;' />""";
        var body = backend == "Jellyfin" ? "{\"Items\":[" + native + "],\"TotalRecordCount\":1,\"StartIndex\":0}"
            : format == "json" ? "{\"subsonic-response\":{\"status\":\"ok\"," +
                (endpoint == "getArtists" ? "\"artists\":{\"index\":[{\"name\":\"N\",\"artist\":[" + native + "]}]}" : "\"starred2\":{\"artist\":[" + native + "]}") + "}}"
            : "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\">" +
                (endpoint == "getArtists" ? "<artists><index name=\"N\">" + native + "</index></artists>" : "<starred2>" + native + "</starred2>") + "</subsonic-response>";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory(backend, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.RequestUri.AbsolutePath == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok"}}""");
            var reply = CatalogNative(format, body, body);
            reply.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=3600");
            return reply;
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin" ? "/Items?ApiKey=fixture&IsFavorite=true"
            : $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.13.0&c=Tempo&f={format}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(native, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FavoriteRoundTrip_JellyfinBooleanFilterTracksBothMutations(bool requestedFavorite)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        using var factory = HeartFactory("Jellyfin", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        foreach (var favorite in new[] { true, false })
        {
            using var mutation = await client.SendAsync(new HttpRequestMessage(favorite ? HttpMethod.Post : HttpMethod.Delete,
                $"/UserFavoriteItems/{HeartSong}?ApiKey=fixture"));
            Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
            using var response = await client.GetAsync($"/Items?ApiKey=fixture&Ids={HeartSong}&IsFavorite={requestedFavorite}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal(favorite == requestedFavorite ? 1 : 0, root.GetProperty("Items").GetArrayLength());
            Assert.Equal(favorite == requestedFavorite ? 1 : 0, root.GetProperty("TotalRecordCount").GetInt32());
        }
    }

    [Fact]
    public async Task FavoriteRoundTrip_EmptyProviderScopePreservesPaging()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ApiKey=fixture&IsFavorite=true&AlbumIds={HeartAlbum}&StartIndex=7&Limit=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(document.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(0, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(7, document.RootElement.GetProperty("StartIndex").GetInt32());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json", "")]
    [InlineData("json", "; charset=utf-8")]
    [InlineData("xml", "")]
    [InlineData("xml", "; charset=utf-8")]
    public async Task FavoriteRoundTrip_SubsonicMergedContentHydratesOnlyMusicNodes(string format, string charset)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = new ProtocolExecutionContext(
            ProtocolKind.Subsonic, "primary", "fixture", new AllstarrPrincipal(FavoriteViewer, "subsonic", "primary", "fixture", "Viewer", false),
            "favorite-content", DateTimeOffset.UtcNow.AddMinutes(1), default);
        const string nativeJson = """{ "id":"native", "title":"Native", "future":{"id":"ext-deezer-song-saved"} }""";
        const string nativeXml = """<song id='native' title='Native'><future id='ext-deezer-song-saved'/></song>""";
        var result = new ContentResult
        {
            ContentType = "application/" + format + charset,
            Content = format == "json"
                ? "{\"subsonic-response\":{\"status\":\"ok\",\"album\":{\"id\":\"native-album\",\"song\":[" + nativeJson + ",{\"id\":\"ext-deezer-song-saved\",\"title\":\"Saved\"}]}}}"
                : "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><album id=\"native-album\">" + nativeXml + "<song id=\"ext-deezer-song-saved\" title=\"Saved\"/></album></subsonic-response>"
        };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(action, [], result, new object());
        await new allstarr.Filters.SubsonicFavoriteResponseFilter(new FavoriteViewReader(storage)).OnResultExecutionAsync(context,
            () => Task.FromResult(new ResultExecutedContext(action, [], context.Result, new object())));
        var body = Assert.IsType<ContentResult>(context.Result).Content!;
        Assert.Contains(format == "json" ? nativeJson : nativeXml, body, StringComparison.Ordinal);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            Assert.True(DateTimeOffset.TryParse(document.RootElement.GetProperty("subsonic-response").GetProperty("album")
                .GetProperty("song")[1].GetProperty("starred").GetString(), out _));
        }
        else
        {
            var song = XDocument.Parse(body).Descendants().Single(item => item.Name.LocalName == "song" && item.Attribute("id")?.Value == "ext-deezer-song-saved");
            Assert.True(DateTimeOffset.TryParse(song.Attribute("starred")?.Value, out _));
        }
    }

    private static string? ReadSubsonicHeart(string body, string format, string id, bool required)
    {
        if (format == "xml")
        {
            var items = XDocument.Parse(body).Descendants().Where(item => item.Attribute("id")?.Value == id).ToArray();
            if (required) Assert.NotEmpty(items);
            var timestamps = items.Select(item => item.Attribute("starred")?.Value).Distinct(StringComparer.Ordinal).ToArray();
            Assert.InRange(timestamps.Length, 0, 1);
            var timestamp = timestamps.SingleOrDefault();
            if (timestamp != null) Assert.True(DateTimeOffset.TryParse(timestamp, out _));
            return timestamp;
        }
        using var document = JsonDocument.Parse(body);
        static IEnumerable<JsonElement> Items(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("id", out _)) yield return value;
                foreach (var property in value.EnumerateObject())
                    foreach (var item in Items(property.Value)) yield return item;
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray())
                    foreach (var item in Items(child)) yield return item;
        }
        var match = Items(document.RootElement).Where(item => item.GetProperty("id").GetString() == id).ToArray();
        if (required) Assert.NotEmpty(match);
        var timestampsJson = match.Select(item => item.TryGetProperty("starred", out var value) ? value.GetString() : null)
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.InRange(timestampsJson.Length, 0, 1);
        var timestampJson = timestampsJson.SingleOrDefault();
        if (timestampJson != null) Assert.True(DateTimeOffset.TryParse(timestampJson, out _));
        return timestampJson;
    }

    private static void AssertHeartUserData(JsonElement data, string id, bool favorite)
    {
        Assert.Equal(id, data.GetProperty("ItemId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("Key").GetString()));
        Assert.Equal(0, data.GetProperty("PlaybackPositionTicks").GetInt64());
        Assert.Equal(0, data.GetProperty("PlayCount").GetInt32());
        Assert.False(data.GetProperty("Played").GetBoolean());
        Assert.Equal(favorite, data.GetProperty("IsFavorite").GetBoolean());
    }

    private static Mock<IProtocolProviderGateway> HeartGateway()
    {
        var song = new Song
        {
            Id = HeartSong,
            ExternalProvider = "deezer",
            ExternalId = "heart",
            Title = "Heart song",
            Artist = "Heart artist",
            ArtistId = HeartArtist,
            Artists = ["Heart artist"],
            ArtistIds = [HeartArtist],
            Album = "Heart album",
            AlbumId = HeartAlbum,
            Genre = "Jazz"
        };
        var album = new Album
        {
            Id = HeartAlbum,
            ExternalProvider = "deezer",
            ExternalId = "heart",
            Title = "Heart album",
            Artist = "Heart artist",
            ArtistId = HeartArtist,
            Genre = "Jazz",
            Songs = [song]
        };
        var artist = new Artist { Id = HeartArtist, ExternalProvider = "deezer", ExternalId = "heart", Name = "Heart artist" };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart")).ReturnsAsync(song);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart")).ReturnsAsync(album);
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart")).ReturnsAsync(artist);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart")).ReturnsAsync([album]);
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart")).ReturnsAsync([song]);
        gateway.Setup(item => item.GetPlaylistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart"))
            .ReturnsAsync(new ExternalPlaylist { Id = "ext-deezer-playlist-heart", Provider = "deezer", ExternalId = "heart", Name = "Hearts" });
        gateway.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "heart")).ReturnsAsync([song]);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "heart", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new SearchResult { Songs = [song], Albums = [album], Artists = [artist] });
        gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), "heart", It.IsAny<int>()))
            .ReturnsAsync([]);
        return gateway;
    }

    private static ProtocolFactory HeartFactory(string backend, FavoriteViewFactory storage, IProtocolProviderGateway gateway) =>
        new(backend, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, JsonSerializer.Serialize(new { Id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query).GetValueOrDefault("ApiKey") == "foreign" ? "foreign" : "user-1" }));
            if (backend == "Jellyfin")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (query.GetValueOrDefault("GenreIds").ToString().Split(',').Any(id => id.StartsWith("genre-", StringComparison.Ordinal)))
                    return Json(400, """{"error":"A native genre identifier must be a UUID"}""");
                return Json(200, """{"Items":[],"TotalRecordCount":0,"StartIndex":0}""");
            }
            var endpoint = request.RequestUri.AbsolutePath.Split('/').Last().Replace(".view", "", StringComparison.Ordinal);
            var container = endpoint switch { "getStarred" => "starred", "getStarred2" => "starred2", "getArtists" => "artists", _ => "searchResult3" };
            var format = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["f"].ToString();
            return CatalogNative(format, """{"subsonic-response":{"status":"ok","version":"1.16.1","CONTAINER":{}}}""".Replace("CONTAINER", container, StringComparison.Ordinal),
                $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"><{container}/></subsonic-response>");
        }, services =>
        {
            ConfigureFavoriteViews(services, storage, gateway);
            foreach (var descriptor in services.Where(item => item.ImplementationType == typeof(SonicPrincipalStartupFilter)).ToArray())
                services.Remove(descriptor);
            services.AddSingleton<IStartupFilter>(new HeartViewerStartupFilter(storage));
            var options = new DurableJobOptions();
            var clock = new SystemPlatformClock();
            var jobs = new DurableJobQueue(storage, options, new JobPayloadPolicy(options), clock);
            services.RemoveAll<IFavoriteActionPipeline>();
            services.AddSingleton<IFavoriteActionPipeline>(new FavoriteActionPipeline(storage, jobs, clock, metadata: gateway));
        });

    private sealed class HeartViewerStartupFilter(FavoriteViewFactory storage) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuePipeline) =>
            {
                var subsonic = context.Request.Path.StartsWithSegments("/rest");
                var foreign = context.Request.Query[subsonic ? "u" : "ApiKey"] == "foreign";
                var principal = foreign ? "foreign" : subsonic ? "fixture" : "user-1";
                await using var db = storage.CreateDbContext();
                var user = await db.Users.SingleAsync(item => item.BackendPrincipalId == principal);
                context.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = new AllstarrPrincipal(
                    user.Id, subsonic ? "subsonic" : "jellyfin", "primary", principal, "Fixture viewer", false);
                await continuePipeline();
            });
            next(app);
        };
    }
}
