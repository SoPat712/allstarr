using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("jellyfin", "json")]
    [InlineData("subsonic", "json")]
    [InlineData("subsonic", "xml")]
    public async Task SharedFavoriteEntersOrdinarySavedBrowseWithoutOwnEventsAndCurrentReheartRestoresIt(string protocol, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        var (viewer, peer, pipeline) = await SeedSharedFavoriteViewAsync(storage, protocol, true);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        const string nativeJellyfin = """{ "Id":"native-one", "Name":"Native", "Type":"Audio", "UserData":{"IsFavorite":false,"future":9} }""";
        const string nativeSonic = """{ "id":"native-one", "title":"Native", "future":{"keep":9} }""";
        const string nativeXml = """<song id="native-one" title="Native"><future keep="9"/></song>""";
        var nativeJson = protocol == "jellyfin" ? $"{{\"Items\":[{nativeJellyfin}],\"TotalRecordCount\":1,\"StartIndex\":0}}" :
            $"{{\"subsonic-response\":{{\"status\":\"ok\",\"randomSongs\":{{\"song\":[{nativeSonic}]}}}}}}";
        using var factory = new ProtocolFactory(protocol == "jellyfin" ? "Jellyfin" : "Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1","Name":"Viewer"}""");
            if (request.RequestUri.AbsolutePath == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            return CatalogNative(format, nativeJson, $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><randomSongs>{nativeXml}</randomSongs></subsonic-response>");
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        var path = protocol == "jellyfin" ? "/Items?ApiKey=fixture&IncludeItemTypes=Audio&Limit=20" :
            $"/rest/getRandomSongs.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&count=20";
        await using (var db = storage.CreateDbContext()) Assert.Equal(0, await db.FavoriteEvents.CountAsync(item => item.OwnerUserId == viewer.Id));
        for (var step = 0; step < 3; step++)
        {
            if (step == 1) await pipeline.RecordAsync(new(FavoriteLinkTests.Fixture.Context(viewer), "ext-applemusic-song-AbC", FavoriteOperation.Unfavorite, "own-unheart"));
            if (step == 2) await pipeline.RecordAsync(new(FavoriteLinkTests.Fixture.Context(peer), "ext-apple-download-song-AbC", FavoriteOperation.Favorite, "peer-reheart"));
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var expectedNative = protocol == "jellyfin" && step == 1 ? JsonSerializer.Serialize(JsonDocument.Parse(nativeJellyfin).RootElement) :
                protocol == "jellyfin" ? nativeJellyfin : format == "json" ? nativeSonic : nativeXml;
            Assert.True(body.Contains(expectedNative, StringComparison.Ordinal), $"Native row changed at step {step}.");
            Assert.DoesNotContain("Partner private display", body, StringComparison.Ordinal);
            if (protocol == "jellyfin")
            {
                using var json = JsonDocument.Parse(body);
                Assert.Equal(step == 1 ? 1 : 2, json.RootElement.GetProperty("TotalRecordCount").GetInt32());
                var provider = json.RootElement.GetProperty("Items").EnumerateArray().Where(item => item.GetProperty("Id").GetString() != "native-one").ToArray();
                Assert.Equal(step == 1 ? 0 : 1, provider.Length);
                Assert.All(provider, item => Assert.True(item.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean()));
            }
            else if (format == "json")
            {
                using var json = JsonDocument.Parse(body);
                var provider = json.RootElement.GetProperty("subsonic-response").GetProperty("randomSongs").GetProperty("song").EnumerateArray()
                    .Where(item => item.GetProperty("id").GetString() != "native-one").ToArray();
                Assert.Equal(step == 1 ? 0 : 1, provider.Length);
                Assert.All(provider, item => Assert.True(item.TryGetProperty("starred", out _)));
            }
            else
            {
                var provider = XDocument.Parse(body).Descendants().Where(item => item.Name.LocalName == "song" && item.Attribute("id")?.Value != "native-one").ToArray();
                Assert.Equal(step == 1 ? 0 : 1, provider.Length);
                Assert.All(provider, item => Assert.NotNull(item.Attribute("starred")));
            }
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("jellyfin", "json", false)]
    [InlineData("jellyfin", "json", true)]
    [InlineData("subsonic", "json", false)]
    [InlineData("subsonic", "json", true)]
    [InlineData("subsonic", "xml", false)]
    [InlineData("subsonic", "xml", true)]
    public async Task SharedHeartProjectionUsesOnlyLinkedProvenAliasesAndKeepsProviderAndExternalIdCaseDistinct(string protocol, string format, bool linked)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSharedFavoriteViewAsync(storage, protocol, linked);
        var savedView = await new SavedLibraryReader(storage).ReadAsync(new(protocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
            "primary", protocol == "jellyfin" ? "user-1" : "fixture", new(FavoriteViewer, protocol, "primary", protocol == "jellyfin" ? "user-1" : "fixture", "Viewer", false),
            "shared-view", DateTimeOffset.UtcNow.AddMinutes(1), default));
        Assert.Equal(linked, FavoriteViewReader.ItemComparer(savedView).Equals("ext-apple-download-song-AbC", "ext-applemusic-song-AbC"));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext context, string provider, string id) => new Song
            { Id = $"ext-{provider}-song-{id}", ExternalProvider = provider, ExternalId = id, Title = "Own current title", Artist = "Own artist" });
        using var factory = HeartFactory(protocol == "jellyfin" ? "Jellyfin" : "Subsonic", storage, gateway.Object);
        using var client = factory.CreateClient();
        foreach (var (id, expected) in new[]
        {
            ("ext-apple-download-song-AbC", true), ("ext-applemusic-song-AbC", linked),
            ("ext-apple-musickit-song-abc", false), ("ext-deezer-song-AbC", false)
        })
        {
            var path = protocol == "jellyfin" ? $"/Users/user-1/Items/{id}?ApiKey=fixture" :
                $"/rest/getSong.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id={id}";
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (protocol == "jellyfin")
            {
                using var json = JsonDocument.Parse(body);
                Assert.Equal(expected, json.RootElement.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());
                if (linked && expected) Assert.True(json.RootElement.TryGetProperty("DateCreated", out _));
                using var played = await client.PostAsync($"/UserPlayedItems/{id}?ApiKey=fixture&UserId=user-1", null);
                Assert.Equal(HttpStatusCode.OK, played.StatusCode);
                using var data = JsonDocument.Parse(await played.Content.ReadAsStringAsync());
                Assert.Equal(expected, data.RootElement.GetProperty("IsFavorite").GetBoolean());
            }
            else if (format == "json")
            {
                using var json = JsonDocument.Parse(body);
                Assert.Equal(expected, json.RootElement.GetProperty("subsonic-response").GetProperty("song").TryGetProperty("starred", out _));
            }
            else Assert.Equal(expected, XDocument.Parse(body).Descendants().Single(item => item.Name.LocalName == "song").Attribute("starred") != null);
            Assert.DoesNotContain("Partner private display", body, StringComparison.Ordinal);
        }
        await using var saved = storage.CreateDbContext();
        if (linked) Assert.Equal(0, await saved.FavoriteEvents.CountAsync(item => item.OwnerUserId == FavoriteViewer));
        Assert.All(await saved.ProviderUserData.ToArrayAsync(), item => Assert.Equal(FavoriteViewer, item.OwnerUserId));
    }

    [Theory]
    [InlineData("jellyfin", "json", false)]
    [InlineData("jellyfin", "json", true)]
    [InlineData("subsonic", "json", false)]
    [InlineData("subsonic", "json", true)]
    [InlineData("subsonic", "xml", false)]
    [InlineData("subsonic", "xml", true)]
    public async Task SharedSavedBrowseDeduplicatesOnlyLinkedAliasesAndKeepsOwnPlaylistMetadataAfterUnheart(string protocol, string format, bool linked)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        var (viewer, _, pipeline) = await SeedSharedFavoriteViewAsync(storage, protocol, linked);
        var context = FavoriteLinkTests.Fixture.Context(viewer);
        var song = new Song
        {
            Id = "ext-apple-download-song-AbC",
            Title = "Own playlist title",
            Artist = "Own artist",
            Artists = ["Own artist"],
            ArtistId = "ext-apple-download-artist-own",
            ArtistIds = ["ext-apple-download-artist-own"],
            Album = "Own album",
            AlbumId = "ext-apple-musickit-album-own"
        };
        var alias = new Song { Id = "ext-apple-musickit-song-AbC", Title = "Own alias title", Artist = "Own artist", Album = "Own album", AlbumId = "ext-apple-download-album-own" };
        var lower = new Song
        {
            Id = "ext-apple-musickit-song-abc",
            Title = "Own lowercase item",
            Artist = "Own artist",
            Artists = ["Own artist"],
            Album = "Own album",
            AlbumId = "ext-apple-download-album-own",
            ArtistId = "ext-apple-musickit-artist-own",
            ArtistIds = ["ext-apple-musickit-artist-own"]
        };
        var other = new Song { Id = "ext-deezer-song-AbC", Title = "Own other source", Artist = "Own artist" };
        var payload = JsonSerializer.Serialize(new WritablePlaylistState([new("one", song.Id, song, "native-one"),
            new("two", alias.Id, alias, "native-two"), new("three", lower.Id, lower, "native-three"), new("four", other.Id, other, "native-four")]));
        var snapshotId = Guid.NewGuid();
        await using (var db = storage.CreateDbContext())
        {
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = snapshotId,
                OwnerUserId = viewer.Id,
                Protocol = protocol,
                BackendInstanceId = viewer.BackendInstanceId,
                BackendPrincipalId = viewer.BackendPrincipalId,
                ProviderId = "allstarr",
                ResourceKind = "playlist-membership",
                ExternalIdHash = WritablePlaylistMembership.Key(context, "own-playlist"),
                SnapshotVersion = 1,
                PayloadJson = payload,
                PayloadSha256 = SavedMembershipHash(payload),
                RetrievedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }
        await pipeline.RecordAsync(new(context, "ext-apple-download-album-own", FavoriteOperation.Favorite, "own-album"));
        await using (var db = storage.CreateDbContext())
        {
            var album = await db.FavoriteStates.SingleAsync(item => item.ItemId == "ext-apple-download-album-own");
            album.DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, album.ItemId, "album", "Own album", ["Own artist"])
            {
                ArtistIds = ["ext-apple-musickit-artist-own", "ext-apple-download-artist-own"],
                AlbumArtistIds = ["ext-apple-musickit-artist-own"]
            }, FavoriteDisplaySnapshot.JsonOptions);
            await db.SaveChangesAsync();
        }
        var reader = new SavedLibraryReader(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory(protocol == "jellyfin" ? "Jellyfin" : "Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.RequestUri.AbsolutePath == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            return protocol == "jellyfin" ? Json(200, """{"Items":[],"TotalRecordCount":0,"StartIndex":0}""") :
                CatalogNative(format, """{"subsonic-response":{"status":"ok","randomSongs":{"song":[]}}}""",
                    """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><randomSongs/></subsonic-response>""");
        }, services =>
        {
            ConfigureFavoriteViews(services, storage, gateway.Object);
            services.RemoveAll<SavedLibraryReader>();
            services.AddSingleton(reader);
        }, configuration: new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = "shared-library" });
        using var client = factory.CreateClient();
        for (var step = 0; step < (linked ? 2 : 1); step++)
        {
            if (step == 1)
            {
                await pipeline.RecordAsync(new(context, "ext-applemusic-song-AbC", FavoriteOperation.Unfavorite, "remove-heart"));
                await pipeline.RecordAsync(new(context, "ext-apple-download-album-own", FavoriteOperation.Unfavorite, "remove-album-heart"));
            }
            var view = await reader.ReadAsync(context);
            var songs = view.Where(item => item.Display.Kind == "song").ToArray();
            Assert.Equal(linked ? 3 : 4, songs.Length);
            Assert.Equal("Own playlist title", Assert.Single(songs, item => item.ItemId == song.Id).Display.Title);
            Assert.Contains(songs, item => item.ItemId == lower.Id);
            Assert.Contains(songs, item => item.ItemId == other.Id);
            Assert.Equal(linked, FavoriteViewReader.ItemComparer(view).Equals(song.Id, alias.Id));
            if (linked)
            {
                var album = Assert.Single(view, item => item.Display.Kind == "album");
                var artist = Assert.Single(view, item => item.Display.Kind == "artist");
                Assert.Equal(2, album.Display.Count);
                Assert.Equal(1, artist.Display.Count);
                static QueryCollection Query(string key, string id) => new(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { [key] = id });
                Assert.Equal(2, songs.Count(item => JellyfinFavoriteScope.Matches(item, Query("AlbumIds", album.ItemId), null)));
                Assert.Equal(2, songs.Count(item => JellyfinFavoriteScope.Matches(item, Query("ParentId", album.ItemId), null)));
                Assert.True(JellyfinFavoriteScope.Matches(album, Query("ArtistIds", artist.ItemId), null));
                Assert.True(JellyfinFavoriteScope.Matches(album, Query("AlbumArtistIds", artist.ItemId), null));
                if (step == 0)
                {
                    var favoriteAlbum = Assert.Single(await new FavoriteViewReader(storage).ReadAsync(context), item => item.Display.Kind == "album");
                    Assert.True(JellyfinFavoriteScope.Matches(favoriteAlbum, Query("AlbumArtistIds", artist.ItemId), null));
                }
            }
            using var response = await client.GetAsync(protocol == "jellyfin" ? "/Items?ApiKey=fixture&IncludeItemTypes=Audio&Limit=20" :
                $"/rest/getRandomSongs.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&count=20");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Own playlist title", body, StringComparison.Ordinal);
            if (protocol == "jellyfin")
            {
                using var json = JsonDocument.Parse(body);
                // General Jellyfin query reconciliation still folds case-only raw IDs.
                Assert.Equal(3, json.RootElement.GetProperty("TotalRecordCount").GetInt32());
                var own = Assert.Single(json.RootElement.GetProperty("Items").EnumerateArray(), item => item.GetProperty("Id").GetString() == song.Id);
                Assert.Equal(step == 0, own.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());
                if (linked)
                {
                    using var albums = await client.GetAsync("/Items?ApiKey=fixture&IncludeItemTypes=MusicAlbum&Fields=ChildCount&Limit=20");
                    Assert.Equal(HttpStatusCode.OK, albums.StatusCode);
                    using var albumJson = JsonDocument.Parse(await albums.Content.ReadAsStringAsync());
                    Assert.Equal(2, Assert.Single(albumJson.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("ChildCount").GetInt32());
                    var album = Assert.Single(view, item => item.Display.Kind == "album");
                    var artist = Assert.Single(view, item => item.Display.Kind == "artist");
                    if (step == 0)
                    {
                        await pipeline.RecordAsync(new(context, song.Id, FavoriteOperation.Favorite, "own-confirmed-metadata"));
                        await using var db = storage.CreateDbContext();
                        var ownState = await db.FavoriteStates.SingleAsync(item => item.OwnerUserId == viewer.Id && item.ItemId == song.Id);
                        ownState.DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, song.Id, "song", song.Title, [song.Artist], song.Album)
                        { AlbumId = "ext-apple-musickit-album-own", ArtistIds = ["ext-apple-musickit-artist-own"], AlbumArtistIds = ["ext-apple-musickit-artist-own"] }, FavoriteDisplaySnapshot.JsonOptions);
                        await db.SaveChangesAsync();
                        var ownFavorite = Assert.Single(await new FavoriteViewReader(storage).ReadAsync(context), item => item.ItemId == song.Id);
                        Assert.NotEqual(album.ItemId, ownFavorite.Display.AlbumId);
                        Assert.NotEqual(artist.ItemId, Assert.Single(ownFavorite.Display.ArtistIds));
                        Assert.Equal(FavoriteLinkScope.Key(album.ItemId), FavoriteLinkScope.Key(ownFavorite.Display.AlbumId!));
                        Assert.Equal(FavoriteLinkScope.Key(artist.ItemId), FavoriteLinkScope.Key(ownFavorite.Display.ArtistIds[0]));
                    }
                    foreach (var relation in new[] { "AlbumIds=" + album.ItemId, "ArtistIds=" + artist.ItemId })
                    {
                        using var children = await client.GetAsync("/Items?ApiKey=fixture&ParentId=shared-library&IncludeItemTypes=Audio&" + relation);
                        using var childJson = JsonDocument.Parse(await children.Content.ReadAsStringAsync());
                        Assert.Equal(2, childJson.RootElement.GetProperty("Items").GetArrayLength());
                        using var favorites = await client.GetAsync("/Items?ApiKey=fixture&IsFavorite=true&ParentId=shared-library&IncludeItemTypes=Audio&" + relation);
                        using var favoriteJson = JsonDocument.Parse(await favorites.Content.ReadAsStringAsync());
                        Assert.Equal(step == 0 ? 1 : 0, favoriteJson.RootElement.GetProperty("Items").GetArrayLength());
                    }
                    using var parentFavorites = await client.GetAsync("/Items?ApiKey=fixture&IsFavorite=true&IncludeItemTypes=Audio&ParentId=" + album.ItemId);
                    using var parentJson = JsonDocument.Parse(await parentFavorites.Content.ReadAsStringAsync());
                    Assert.Equal(step == 0 ? 1 : 0, parentJson.RootElement.GetProperty("Items").GetArrayLength());
                }
            }
            else if (format == "json")
            {
                using var json = JsonDocument.Parse(body);
                var own = Assert.Single(json.RootElement.GetProperty("subsonic-response").GetProperty("randomSongs").GetProperty("song").EnumerateArray(), item => item.GetProperty("id").GetString() == song.Id);
                Assert.Equal(step == 0, own.TryGetProperty("starred", out _));
            }
            else
            {
                var own = Assert.Single(XDocument.Parse(body).Descendants(), item => item.Attribute("id")?.Value == song.Id);
                Assert.Equal(step == 0, own.Attribute("starred") != null);
            }
        }
        await using var saved = storage.CreateDbContext();
        Assert.Equal(payload, (await saved.ExternalMetadataSnapshots.SingleAsync(item => item.Id == snapshotId)).PayloadJson);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FavoriteQueryKeepsOpaqueExternalIdCaseExactForLinkedAndUnlinkedViews(bool linked)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSharedFavoriteViewAsync(storage, "jellyfin", linked);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ProtocolExecutionContext context, string query, int songs, int albums, int artists, string? provider) => new SearchResult
            {
                Songs = [new() { Id = "ext-apple-download-song-" + (query == "linked upper" ? "AbC" : "abc"), ExternalProvider = "apple-download",
                ExternalId = query == "linked upper" ? "AbC" : "abc", Title = query, Artist = "Artist" }]
            });
        gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync([]);
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        foreach (var upper in new[] { true, false })
        {
            using var response = await client.GetAsync("/Items?ApiKey=fixture&SearchTerm=linked%20" + (upper ? "upper" : "lower") + "&IncludeItemTypes=Audio&Limit=20");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var row = Assert.Single(json.RootElement.GetProperty("Items").EnumerateArray());
            Assert.Equal(upper, row.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());
        }
    }

    private static async Task<(UserRecord Viewer, UserRecord Peer, FavoriteActionPipeline Pipeline)> SeedSharedFavoriteViewAsync(FavoriteViewFactory storage, string protocol, bool linked)
    {
        var now = DateTimeOffset.UtcNow;
        var viewer = new UserRecord
        {
            Id = FavoriteViewer,
            BackendType = protocol,
            BackendInstanceId = "primary",
            BackendPrincipalId = protocol == "jellyfin" ? "user-1" : "fixture",
            DisplayName = "Viewer",
            Enabled = true,
            IsAdmin = true,
            LastSeenAt = now
        };
        var peer = FavoriteLinkTests.Fixture.Copy(viewer, Guid.NewGuid(), "linked-peer", protocol == "jellyfin" ? "subsonic" : "jellyfin", false);
        await using (var db = storage.CreateDbContext()) { db.Users.AddRange(viewer, peer); await db.SaveChangesAsync(); }
        var clock = new SystemPlatformClock();
        var queue = new DurableJobQueue(storage, new(), new JobPayloadPolicy(new()), clock);
        var pipeline = new FavoriteActionPipeline(storage, queue, clock);
        if (linked)
        {
            var first = protocol == "jellyfin" ? viewer : peer;
            var second = protocol == "subsonic" ? viewer : peer;
            await new FavoriteLinkService(storage, queue, clock).CreateAsync(FavoriteLinkTests.Fixture.Session(viewer, true),
                new(Guid.NewGuid(), first.Id, FavoriteLinkScope.Fingerprint(first), second.Id, FavoriteLinkScope.Fingerprint(second), true, 1), default);
        }
        var source = linked ? peer : viewer;
        await pipeline.RecordAsync(new(FavoriteLinkTests.Fixture.Context(source), "ext-apple-download-song-AbC", FavoriteOperation.Favorite, "heart"));
        await using (var db = storage.CreateDbContext())
        {
            var state = await db.FavoriteStates.SingleAsync();
            state.DisplayMetadataJson = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, state.ItemId, "song", "Partner private display", []), FavoriteDisplaySnapshot.JsonOptions);
            await db.SaveChangesAsync();
        }
        return (viewer, peer, pipeline);
    }
}
