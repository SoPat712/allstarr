using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinMixedPlaylist_FilteredPagesKeepOccurrenceIdsForMoveAndRemoval()
    {
        const string playlistId = "4383a46d8bcac3be2ef9385053ea18df";
        const string native = """{ "Id": "4383a46d8bcac3be2ef9385053ea18aa", "PlaylistItemId": "native-entry", "Name": "Track Native", "Type": "Audio", "AlbumId": "native-album", "ParentId": "native-parent", "Future": { "Keep": true } }""";
        var gateway = MixedPlaylistGateway();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1","Name":"Owner"}""");
            if (path == "/Playlists" && request.Method == HttpMethod.Post)
            {
                var dto = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                Assert.Equal(["4383a46d8bcac3be2ef9385053ea18aa"], dto["Ids"]!.AsArray().Select(id => id!.GetValue<string>()));
                return Json(200, $$"""{"Id":"{{playlistId}}"}""");
            }
            Assert.Equal($"/Playlists/{playlistId}/Items", path);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (request.Method == HttpMethod.Post)
            {
                Assert.False(query.ContainsKey("Ids"));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.False(query.ContainsKey("StartIndex"));
            Assert.False(query.ContainsKey("Limit"));
            Assert.False(query.ContainsKey("SearchTerm"));
            Assert.False(query.ContainsKey("ExcludeItemIds"));
            Assert.False(query.ContainsKey("SortBy"));
            return Json(200, $$"""{"Items":[{{native}}],"TotalRecordCount":1,"StartIndex":0,"FutureEnvelope":true}""");
        }, services => ConfigureMixedPlaylists(services, gateway, playlistId));
        using var client = factory.CreateClient();
        await SeedMixedPlaylistUserAsync(factory, "jellyfin", "user-1");
        using var created = await client.PostAsync("/Playlists?api_key=fixture-key", new StringContent(
            """{"Name":"Mixed","Ids":["4383a46d8bcac3be2ef9385053ea18aa","ext-deezer-song-A","ext-deezer-song-A","ext-deezer-song-B","ext-deezer-song-C"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        async Task<JsonObject[]> Read(int total, string query = "", int start = 0, int limit = 20)
        {
            using var response = await client.GetAsync(
                $"/Playlists/{playlistId}/Items?api_key=fixture-key&Fields=ParentId&StartIndex={start}&Limit={limit}&{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(text);
            Assert.Equal(total, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
            Assert.Equal(start, body.RootElement.GetProperty("StartIndex").GetInt32());
            Assert.True(body.RootElement.GetProperty("FutureEnvelope").GetBoolean());
            foreach (var row in body.RootElement.GetProperty("Items").EnumerateArray().Where(row => row.GetProperty("Id").GetString() == "4383a46d8bcac3be2ef9385053ea18aa"))
                Assert.Equal(native, row.GetRawText());
            return JsonNode.Parse(text)!["Items"]!.AsArray().Select(row => row!.AsObject()).ToArray();
        }
        var initial = await Read(5);
        var ids = initial.Select(row => row["PlaylistItemId"]!.GetValue<string>()).ToArray();
        Assert.Equal(5, ids.Distinct().Count());
        const string filter = "SearchTerm=Track&ExcludeItemIds=ext-deezer-song-C&SortBy=SortName&SortOrder=Descending";
        var sorted = await Read(5, filter);
        Assert.Equal([ids[0], ids[3], ids[1], ids[2]], sorted.Select(row => row["PlaylistItemId"]!.GetValue<string>()));
        var first = Assert.Single(await Read(5, filter, 2, 1));
        var second = Assert.Single(await Read(5, filter, 3, 1));
        var firstEntry = first["PlaylistItemId"]!.GetValue<string>();
        var secondEntry = second["PlaylistItemId"]!.GetValue<string>();
        Assert.Equal(ids[1], firstEntry);
        Assert.Equal(ids[2], secondEntry);
        Assert.Equal(playlistId, second["ParentId"]!.GetValue<string>());
        Assert.Equal("ext-deezer-album-album", second["AlbumId"]!.GetValue<string>());
        Assert.Empty(await Read(5, filter, 1, 0));
        Assert.Empty(await Read(5, filter, 4, 1));
        var repeated = Assert.Single(await Read(5, filter, 3, 1));
        Assert.Equal(ids[2], repeated["PlaylistItemId"]!.GetValue<string>());
        var snapshots = await MembershipSnapshotCountAsync(factory);
        using var moved = await client.PostAsync($"/Playlists/{playlistId}/Items/{secondEntry}/Move/0?api_key=fixture-key", null);
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        Assert.Equal([ids[2], ids[0], ids[1], ids[3], ids[4]], (await Read(5)).Select(row => row["PlaylistItemId"]!.GetValue<string>()));
        using var removed = await client.DeleteAsync($"/Playlists/{playlistId}/Items?EntryIds={firstEntry}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal([ids[2], ids[0], ids[3], ids[4]], (await Read(4)).Select(row => row["PlaylistItemId"]!.GetValue<string>()));
        Assert.Equal([ids[0], ids[3], ids[2]], (await Read(4, filter)).Select(row => row["PlaylistItemId"]!.GetValue<string>()));
        Assert.Equal(snapshots + 2, await MembershipSnapshotCountAsync(factory));
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.Exactly(3));
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlaylistOccurrence_ProviderFilteringAndSortingKeepIdsFromFullMembership()
    {
        const string playlistId = "ext-deezer-playlist-query";
        var tracks = new List<Song>
        {
            QuerySong("b", "Keep Beta", 2020, "Rock"),
            QuerySong("hidden", "Hidden", 2019, "Jazz"),
            QuerySong("a", "Keep Alpha", 2020, "Rock"),
            QuerySong("b", "Keep Beta", 2020, "Rock")
        };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPlaylistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "query"))
            .ReturnsAsync(tracks);
        using var factory = ProviderQueryFactory(gateway.Object);
        using var client = factory.CreateClient();
        async Task<JsonElement[]> Read(string query, int start, int limit)
        {
            using var response = await client.GetAsync($"/Playlists/{playlistId}/Items?api_key=fixture-key&{query}&StartIndex={start}&Limit={limit}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(4, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
            Assert.Equal(start, body.RootElement.GetProperty("StartIndex").GetInt32());
            return body.RootElement.GetProperty("Items").EnumerateArray().Select(row => row.Clone()).ToArray();
        }
        var initial = await Read("", 0, 20);
        var ids = initial.Select(row => row.GetProperty("PlaylistItemId").GetString()).ToArray();
        Assert.Equal(4, ids.Distinct().Count());
        const string filter = "SearchTerm=Keep&Genres=Rock&SortBy=SortName&SortOrder=Ascending";
        var sorted = await Read(filter, 0, 20);
        Assert.Equal([ids[2], ids[0], ids[3]], sorted.Select(row => row.GetProperty("PlaylistItemId").GetString()));
        for (var start = 0; start < sorted.Length; start++)
        {
            var row = Assert.Single(await Read(filter, start, 1));
            Assert.Equal(sorted[start].GetProperty("PlaylistItemId").GetString(), row.GetProperty("PlaylistItemId").GetString());
        }
        Assert.Empty(await Read(filter, 3, 1));
        Assert.Empty(await Read(filter, 1, 0));
    }

    [Fact]
    public async Task PlaylistOccurrence_ProviderPagesHaveDistinctStableEntriesAndPlaylistRelationships()
    {
        const string playlistId = "ext-deezer-playlist-occurrences";
        var song = new Song
        {
            Id = "ext-deezer-song-42",
            ExternalProvider = "deezer",
            ExternalId = "42",
            Title = "Repeated",
            Artist = "Artist",
            Album = "Original album",
            AlbumId = "ext-deezer-album-9"
        };
        var tracks = new List<Song> { song, song, song };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPlaylistTracksAsync(
            It.IsAny<ProtocolExecutionContext>(), "deezer", "occurrences"))
            .ReturnsAsync(tracks);
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();
        var entries = new List<string>();
        for (var start = 0; start < 3; start++)
        {
            using var response = await client.GetAsync(
                $"/Playlists/{playlistId}/Items?StartIndex={start}&Limit=1&Fields=ParentId&api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(3, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
            Assert.Equal(start, body.RootElement.GetProperty("StartIndex").GetInt32());
            var item = Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray());
            Assert.Equal(song.Id, item.GetProperty("Id").GetString());
            Assert.Equal(playlistId, item.GetProperty("AlbumId").GetString());
            Assert.Equal(playlistId, item.GetProperty("AlbumPrimaryImageTag").GetString());
            Assert.Equal(playlistId, item.GetProperty("ParentId").GetString());
            entries.Add(item.GetProperty("PlaylistItemId").GetString()!);
        }
        Assert.Equal(3, entries.Distinct().Count());
        using var repeated = await client.GetAsync(
            $"/Playlists/{playlistId}/Items?StartIndex=1&Limit=1&api_key=fixture-key");
        using var repeatedBody = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.Equal(entries[1], Assert.Single(repeatedBody.RootElement.GetProperty("Items").EnumerateArray())
            .GetProperty("PlaylistItemId").GetString());
        using var empty = await client.GetAsync(
            $"/Playlists/{playlistId}/Items?StartIndex=3&Limit=1&api_key=fixture-key");
        using var emptyBody = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Empty(emptyBody.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(3, emptyBody.RootElement.GetProperty("TotalRecordCount").GetInt32());
        tracks.Insert(0, new Song { Id = "ext-deezer-song-99", Title = "Inserted", Artist = "Artist" });
        using var shifted = await client.GetAsync(
            $"/Playlists/{playlistId}/Items?StartIndex=2&Limit=1&api_key=fixture-key");
        using var shiftedBody = JsonDocument.Parse(await shifted.Content.ReadAsStringAsync());
        Assert.Equal(entries[1], Assert.Single(shiftedBody.RootElement.GetProperty("Items").EnumerateArray())
            .GetProperty("PlaylistItemId").GetString());
    }

    [Fact]
    public async Task PlaylistOccurrence_ProviderDefinitionIsReadOnly()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPlaylistTracksAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", "occurrences"))
            .ReturnsAsync(new List<Song>());
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(StatusCodes.Status200OK, """{"Id":"verified-user"}""")
                : throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"),
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Playlists/ext-deezer-playlist-occurrences?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("CanEdit").GetBoolean());
        using var permissions = await client.GetAsync("/Playlists/ext-deezer-playlist-occurrences/Users/verified-user?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, permissions.StatusCode);
        using var permissionBody = JsonDocument.Parse(await permissions.Content.ReadAsStringAsync());
        Assert.Equal("verified-user", permissionBody.RootElement.GetProperty("UserId").GetString());
        Assert.False(permissionBody.RootElement.GetProperty("CanEdit").GetBoolean());
    }
}
