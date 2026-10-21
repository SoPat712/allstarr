using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    public static TheoryData<string, string, string> ProviderSongCollections => new()
    {
        { "search3", "searchResult3", "song" },
        { "search2", "searchResult2", "song" },
        { "getSong", "", "song" },
        { "getAlbum", "album", "song" },
        { "getMusicDirectory", "directory", "child" },
        { "getSimilarSongs2", "similarSongs2", "song" },
        { "getSimilarSongs", "similarSongs", "song" },
        { "getTopSongs", "topSongs", "song" }
    };

    [Theory]
    [MemberData(nameof(ProviderSongCollections))]
    public async Task FeishinAndTempo_ProviderSongCollectionsNormalizeInBothFormats(
        string method, string collection, string entry)
    {
        foreach (var format in new[] { "json", "xml" })
        {
            var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
            var song = new Song
            {
                Id = "ext-deezer-song-42",
                ExternalProvider = "deezer",
                ExternalId = "42",
                Title = "Sparse",
                Artist = "Fixture"
            };
            if (method is "search3" or "search2" or "getTopSongs")
                gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Fixture",
                    It.IsAny<int>(), 0, 0, null)).ReturnsAsync(new SearchResult { Songs = [song] });
            else if (method is "getAlbum" or "getMusicDirectory")
                gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
                    .ReturnsAsync(new Album { Id = "ext-deezer-album-7", Title = "Album", Songs = [song] });
            else
            {
                var seed = method.StartsWith("getSimilar", StringComparison.Ordinal)
                    ? new Song { Id = "ext-deezer-song-41", ExternalProvider = "deezer", ExternalId = "41", Title = "Seed" }
                    : song;
                gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
                    .ReturnsAsync(seed);
                if (method.StartsWith("getSimilar", StringComparison.Ordinal))
                    gateway.Setup(service => service.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(),
                        "deezer", "41", It.IsAny<int>())).ReturnsAsync([new Song
                        {
                            Id = "ext-deezer-song-43", ExternalProvider = "deezer", ExternalId = "43", Title = "Sparse"
                        }]);
            }
            var nativeJson = $$$$"""{"subsonic-response":{"status":"ok","version":"1.16.1","{{{{collection}}}}":{"{{{{entry}}}}":[{"id":"native","title":"Native","artist":"Native artist","contentType":"audio/flac","suffix":"flac","size":5000,"future":{"keep":9}}]}}}""";
            var nativeXml = $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"><{collection}><{entry} id=\"native\" title=\"Native\" artist=\"Native artist\" contentType=\"audio/flac\" suffix=\"flac\" size=\"5000\" future=\"kept\" /></{collection}></subsonic-response>";
            var observed = new List<ObservedRequest>();
            using var factory = CatalogFactory(observed, gateway.Object, _ => CatalogNative(format, nativeJson, nativeXml));
            using var client = factory.CreateClient();
            var id = method is "getAlbum" or "getMusicDirectory" ? "ext-deezer-album-7" : song.Id;
            var parameters = new Dictionary<string, string>
            {
                ["u"] = "fixture",
                ["p"] = "fixture-password",
                ["v"] = "1.13.0",
                ["c"] = "Feishin",
                ["f"] = format,
                ["id"] = id,
                ["query"] = "Fixture",
                ["artist"] = "Fixture",
                ["songCount"] = "20",
                ["songOffset"] = "0",
                ["albumCount"] = "0",
                ["artistCount"] = "0",
                ["count"] = "2"
            };
            using var response = format == "json"
                ? await client.PostAsync($"/rest/{method}.view", new FormUrlEncodedContent(parameters))
                : await client.GetAsync($"/rest/{method}.view?u=fixture&p=fixture-password&v=1.16.1&c=tempo&f=xml&id={id}&query=Fixture&artist=Fixture&songCount=20&albumCount=0&artistCount=0&count=2");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var songs = ReadSongCollection(body, format, collection, entry);
            var expectedId = method.StartsWith("getSimilar", StringComparison.Ordinal) ? "ext-deezer-song-43" : song.Id;
            var provider = Assert.Single(songs, item => item["id"] == expectedId);
            SubsonicSongMediaContractTests.AssertSparseMedia(provider);
            if (method is "search3" or "search2" or "getTopSongs")
            {
                var native = Assert.Single(songs, item => item["id"] == "native");
                Assert.Equal("audio/flac", native["contentType"]);
                Assert.Equal("flac", native["suffix"]);
                Assert.Equal("5000", native["size"]);
                Assert.True(native.ContainsKey("future"));
            }
            gateway.VerifyAll();
            gateway.VerifyNoOtherCalls();
        }
    }

    [Theory]
    [InlineData("getStarred", "starred")]
    [InlineData("getStarred2", "starred2")]
    public async Task FeishinAndTempo_SavedSongMediaNeedsNoProviderCallsOrWrites(string endpoint, string collection)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        var readStorage = new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(new SongReadWriteGuard()).Options);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        foreach (var format in new[] { "json", "xml" })
        {
            using var factory = new ProtocolFactory("Subsonic", request =>
                request.RequestUri!.AbsolutePath == "/rest/ping.view"
                    ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
                    : CatalogNative(format,
                        $$$$"""{"subsonic-response":{"status":"ok","version":"1.16.1","{{{{collection}}}}":{}}}""",
                        $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"><{collection}/></subsonic-response>"),
                services => ConfigureFavoriteViews(services, readStorage, gateway.Object));
            using var client = factory.CreateClient();
            using var response = await client.GetAsync($"/rest/{endpoint}.view?u=fixture&p=fixture-password&v=1.13.0&c=Feishin&f={format}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var songs = ReadSongCollection(await response.Content.ReadAsStringAsync(), format, collection, "song");
            Assert.NotEmpty(songs);
            Assert.All(songs, SubsonicSongMediaContractTests.AssertSparseMedia);
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task FeishinAndTempo_MixedPlaylistMediaNeedsNoProviderCallsOrWrites(string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedFavoriteViewsAsync(new FavoriteViewFactory(database.Options), "subsonic");
        var storage = new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options)
            .AddInterceptors(new SongReadWriteGuard()).Options);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        const string native = """{"id":"native","title":"Native","contentType":"audio/flac","suffix":"flac","size":4500,"future":"kept"}""";
        var linkId = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0");
        var id = PlaylistVirtualizationService.CreateProtocolId(linkId);
        var playlist = new VirtualPlaylistReadModel(id, linkId, Guid.NewGuid(), "Fixture", null, null,
            "spotify", "fixture", "1", PlaylistLinkMode.Virtual,
            [
                new(0, "native", "Native", "Artist", null, null, null, null,
                    TrackMatchState.Accepted, RouteKind: TrackRouteKind.Local, NativeEntryJson: native),
                new(1, "ext-deezer-song-42", "Sparse", "Artist", null, null, null, null,
                    TrackMatchState.Accepted, RouteKind: TrackRouteKind.External)
            ]);
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        playlists.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(playlist);
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
            : throw new InvalidOperationException("Unexpected native playlist lookup"), services =>
        {
            ConfigureFavoriteViews(services, storage, gateway.Object);
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(playlists.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/getPlaylist.view?u=fixture&p=fixture-password&v=1.16.1&c=tempo&f={format}&id={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var songs = ReadSongCollection(body, format, "playlist", "entry");
        Assert.Equal(2, songs.Length);
        SubsonicSongMediaContractTests.AssertSparseMedia(songs[1]);
        Assert.Equal("audio/flac", songs[0]["contentType"]);
        Assert.Equal("4500", songs[0]["size"]);
        Assert.Equal("kept", songs[0]["future"]);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(native, document.RootElement.GetProperty("subsonic-response")
                .GetProperty("playlist").GetProperty("entry")[0].GetRawText());
        }
        playlists.VerifyAll();
        playlists.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("getRandomSongs", "randomSongs", "song")]
    [InlineData("getSongsByGenre", "songsByGenre", "song")]
    [InlineData("getPlayQueue", "playQueue", "entry")]
    [InlineData("getPlayQueueByIndex", "playQueue", "entry")]
    [InlineData("getNowPlaying", "nowPlaying", "entry")]
    [InlineData("getPlaylist", "playlist", "entry")]
    public async Task SubsonicSongRelays_PreserveNativeBytesAndDoNotDiscoverProviderSongs(
        string endpoint, string collection, string entry)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        foreach (var format in new[] { "json", "xml" })
        {
            var nativeJson = $$$$"""{ "subsonic-response": {"status":"ok","version":"1.16.1","{{{{collection}}}}":{"{{{{entry}}}}":[ {"id":"native","title":"Native","contentType":"audio/ogg","suffix":"ogg","bitRate":128,"size":5000,"future":[1,2]} ]}} }""";
            var nativeXml = $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\">\n <{collection}><{entry} id=\"native\" title=\"Native\" contentType=\"audio/ogg\" suffix=\"ogg\" bitRate=\"128\" size=\"5000\"><future>kept</future></{entry}></{collection}>\n</subsonic-response>";
            using var factory = CatalogFactory([], gateway.Object, _ => CatalogNative(format, nativeJson, nativeXml));
            using var client = factory.CreateClient();
            using var response = await client.GetAsync($"/rest/{endpoint}.view?u=fixture&p=fixture-password&v=1.16.1&c=tempo&f={format}&size=20&genre=Rock");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(format == "json" ? nativeJson : nativeXml, await response.Content.ReadAsStringAsync());
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Finamp", "ChildCount,DateCreated,DateLastMediaAdded,Etag,Genres,ParentId,ProviderIds,Tags,SortName")]
    [InlineData("Jellyfin Web", "ParentId")]
    public async Task JellyfinSongClients_PreserveNativeDtoTypesAndMediaFacts(string clientName, string fields)
    {
        const string native = """{"Items":[{"Id":"578f3ede01c44ed6ab4d5d72d5123941","ServerId":"fixture-server","Name":"Native","Type":"Audio","MediaType":"Audio","RunTimeTicks":1800000000,"UserData":{"PlaybackPositionTicks":0,"PlayCount":4,"IsFavorite":true,"Played":false},"MediaSources":[{"Id":"native-source","Name":"Native","Container":"ogg","Size":4500,"Bitrate":128000,"MediaStreams":[{"Type":"Audio","Codec":"opus","BitRate":128000}]}],"future":{"keep":9}}],"TotalRecordCount":1,"StartIndex":0}""";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""") : Json(200, native), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/Users/user-1/Items?IncludeItemTypes=Audio&Recursive=true&SortBy=Album,SortName&SortOrder=Ascending&StartIndex=0&Limit=50&Fields={fields}&ApiKey=fixture-key");
        request.Headers.Add("X-Emby-Authorization", $"MediaBrowser Client=\"{clientName}\", Device=\"Fixture\", DeviceId=\"fixture\", Version=\"1\"");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(native, body);
        using var document = JsonDocument.Parse(body);
        var item = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal("Audio", item.GetProperty("Type").GetString());
        Assert.Equal(1800000000L, item.GetProperty("RunTimeTicks").GetInt64());
        var userData = item.GetProperty("UserData");
        Assert.Equal(0L, userData.GetProperty("PlaybackPositionTicks").GetInt64());
        Assert.Equal(4, userData.GetProperty("PlayCount").GetInt32());
        Assert.True(userData.GetProperty("IsFavorite").GetBoolean());
        Assert.False(userData.GetProperty("Played").GetBoolean());
        var source = item.GetProperty("MediaSources")[0];
        Assert.Equal("ogg", source.GetProperty("Container").GetString());
        Assert.Equal(4500L, source.GetProperty("Size").GetInt64());
        Assert.Equal(128000, source.GetProperty("Bitrate").GetInt32());
        Assert.Equal("opus", source.GetProperty("MediaStreams")[0].GetProperty("Codec").GetString());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("flac", "audio/flac", "flac", true)]
    [InlineData("mp3", "audio/mpeg", "mp3", true)]
    [InlineData("m4a", "audio/mp4", "aac", true)]
    [InlineData("flac", "audio/flac", "flac", false)]
    [InlineData("mp3", "audio/mpeg", "mp3", false)]
    [InlineData("m4a", "audio/mp4", "aac", false)]
    public async Task SubsonicSongMedia_DoesNotRelabelTheSelectedStream(
        string suffix, string contentType, string codec, bool knownFormat)
    {
        byte[] bytes = [1, 2, 3, 4];
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-42",
                ExternalProvider = "deezer",
                ExternalId = "42",
                Title = "Fixture",
                LocalPath = knownFormat ? $"/fixture/song.{suffix}" : null
            });
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42",
                It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync(() =>
            {
                var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                upstream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                return new ProtocolProviderStream(upstream,
                    new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                        true, true, new(contentType, suffix, codec), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42");
            });
        using var factory = CatalogFactory([], gateway.Object, _ => throw new InvalidOperationException("Unexpected native stream"));
        using var client = factory.CreateClient();
        const string authentication = "u=fixture&p=fixture-password&v=1.13.0&c=Feishin&id=ext-deezer-song-42";
        foreach (var format in new[] { "json", "xml" })
        {
            using var detail = await client.GetAsync($"/rest/getSong.view?{authentication}&f={format}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var song = Assert.Single(ReadSongCollection(await detail.Content.ReadAsStringAsync(), format, "", "song"));
            if (knownFormat)
            {
                Assert.Equal(contentType, song["contentType"]);
                Assert.Equal(suffix, song["suffix"]);
            }
            else SubsonicSongMediaContractTests.AssertSparseMedia(song);
        }
        using var stream = await client.GetAsync($"/rest/stream.view?{authentication}&format=raw&maxBitRate=0");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(contentType, stream.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await stream.Content.ReadAsByteArrayAsync());
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.Exactly(2));
        gateway.Verify(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42",
            It.IsAny<ProviderAudioQuality>(), null, false), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    private static Dictionary<string, string?>[] ReadSongCollection(string body, string format, string collection, string entry)
    {
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            if (collection.Length == 0) return [SubsonicSongMediaContractTests.ReadJson(root.GetProperty(entry))];
            return root.GetProperty(collection).GetProperty(entry).EnumerateArray()
                .Select(SubsonicSongMediaContractTests.ReadJson).ToArray();
        }
        return XDocument.Parse(body).Descendants().Where(element => element.Name.LocalName == entry)
            .Select(SubsonicSongMediaContractTests.ReadXml).ToArray();
    }

    private sealed class SongReadWriteGuard : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new InvalidOperationException("Song reads must not save changes.");

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Song reads must not save changes.");
    }
}
