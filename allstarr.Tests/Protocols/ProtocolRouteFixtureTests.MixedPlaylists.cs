using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Feishin", "json", true)]
    [InlineData("Tempo", "xml", false)]
    [InlineData("Tempo", "json", false)]
    [InlineData("Feishin", "json", false)]
    [InlineData("Feishin", "xml", true)]
    public async Task SubsonicMixedPlaylist_PersistsRepeatedMembersAndUsesReturnedRemovalIndices(
        string clientName, string format, bool form)
    {
        var backend = new List<string>();
        var owner = "fixture";
        var isPublic = false;
        var gateway = MixedPlaylistGateway();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            var body = request.Content == null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            foreach (var pair in QueryHelpers.ParseQuery(body)) query[pair.Key] = pair.Value;
            var method = request.RequestUri.AbsolutePath.Split('/').Last().Replace(".view", "");
            if (method == "ping") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (method == "createPlaylist")
            {
                backend = query.GetValueOrDefault("songId").ToArray().Select(id => id!).ToList();
                Assert.DoesNotContain(backend, id => id.StartsWith("ext-", StringComparison.Ordinal));
            }
            if (method == "updatePlaylist")
            {
                if (bool.TryParse(query.GetValueOrDefault("public"), out var visibility)) isPublic = visibility;
                foreach (var index in query.GetValueOrDefault("songIndexToRemove").Select(value => int.Parse(value!)).OrderDescending()) backend.RemoveAt(index);
                backend.AddRange(query.GetValueOrDefault("songIdToAdd").Select(id => id!));
                Assert.DoesNotContain(backend, id => id.StartsWith("ext-", StringComparison.Ordinal));
            }
            if (method == "deletePlaylist") backend.Clear();
            var playlist = new JsonObject
            {
                ["id"] = "native-playlist",
                ["name"] = "Mixed",
                ["owner"] = owner,
                ["public"] = isPublic,
                ["songCount"] = backend.Count,
                ["entry"] = new JsonArray(backend.Select(id => (JsonNode)new JsonObject
                { ["id"] = id, ["title"] = "Native", ["contentType"] = "audio/flac", ["future"] = "keep" }).ToArray())
            };
            var root = new JsonObject { ["status"] = "ok", ["version"] = "1.16.1" };
            if (method is "createPlaylist" or "getPlaylist") root["playlist"] = playlist;
            if (format == "json" || query["f"] == "json")
                return Json(200, new JsonObject { ["subsonic-response"] = root }.ToJsonString());
            var ns = XNamespace.Get("http://subsonic.org/restapi");
            var xml = new XElement(ns + "subsonic-response", new XAttribute("status", "ok"), new XAttribute("version", "1.16.1"));
            if (method is "createPlaylist" or "getPlaylist")
                xml.Add(new XElement(ns + "playlist", new XAttribute("id", "native-playlist"), new XAttribute("name", "Mixed"),
                    new XAttribute("owner", owner), new XAttribute("public", isPublic), new XAttribute("songCount", backend.Count),
                    backend.Select(id => new XElement(ns + "entry", new XAttribute("id", id), new XAttribute("title", "Native"),
                        new XAttribute("contentType", "audio/flac"), new XAttribute("future", "keep")))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
        }, services => ConfigureMixedPlaylists(services, gateway));
        using var client = factory.CreateClient();
        await SeedMixedPlaylistUserAsync(factory, "subsonic", "fixture");
        var authentication = $"u=fixture&p=secret&v=1.16.1&c={clientName}&f={format}";
        async Task<string> Send(string method, string values)
        {
            using var request = new HttpRequestMessage(form ? HttpMethod.Post : HttpMethod.Get,
                $"/rest/{method}.view?{authentication}" + (form ? "" : "&" + values));
            if (form) request.Content = new StringContent(values, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            if (format == "json") Assert.Equal("ok", JsonNode.Parse(text)!["subsonic-response"]!["status"]!.GetValue<string>());
            else Assert.Equal("ok", XDocument.Parse(text).Root!.Attribute("status")!.Value);
            return text;
        }
        string[] Entries(string text) => format == "json"
            ? JsonNode.Parse(text)!["subsonic-response"]!["playlist"]!["entry"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToArray()
            : XDocument.Parse(text).Descendants().Where(item => item.Name.LocalName == "entry").Select(item => item.Attribute("id")!.Value).ToArray();
        await Send("createPlaylist", "name=Mixed&songId=native-song&songId=ext-deezer-song-A&songId=ext-deezer-song-A&songId=ext-deezer-song-B");
        Assert.Equal(["native-song", "ext-deezer-song-A", "ext-deezer-song-A", "ext-deezer-song-B"],
            Entries(await Send("getPlaylist", "id=native-playlist")));
        await Send("updatePlaylist", "playlistId=native-playlist&public=true");
        var publicRead = await Send("getPlaylist", "id=native-playlist");
        if (format == "json") Assert.True(JsonNode.Parse(publicRead)!["subsonic-response"]!["playlist"]!["public"]!.GetValue<bool>());
        else Assert.Equal("true", XDocument.Parse(publicRead).Descendants().Single(item => item.Name.LocalName == "playlist").Attribute("public")!.Value);
        await Send("updatePlaylist", "playlistId=native-playlist&songIndexToRemove=2");
        Assert.Equal(["native-song", "ext-deezer-song-A", "ext-deezer-song-B"], Entries(await Send("getPlaylist", "id=native-playlist")));
        await Send("createPlaylist", "playlistId=native-playlist&songId=ext-deezer-song-B&songId=native-song&songId=ext-deezer-song-A");
        Assert.Equal(["ext-deezer-song-B", "native-song", "ext-deezer-song-A"], Entries(await Send("getPlaylist", "id=native-playlist")));
        await Send("updatePlaylist", "playlistId=native-playlist&songIdToAdd=ext-deezer-song-A&songIdToAdd=native-other");
        Assert.Equal(["ext-deezer-song-B", "native-song", "ext-deezer-song-A", "ext-deezer-song-A", "native-other"],
            Entries(await Send("getPlaylist", "id=native-playlist")));
        const string virtualId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        await Send("createPlaylist", $"playlistId={virtualId}&songId=ext-deezer-song-B&songId=native-song&songId=ext-deezer-song-A&songId=ext-deezer-song-A&songId=native-other");
        Assert.Equal(["ext-deezer-song-B", "native-song", "ext-deezer-song-A", "ext-deezer-song-A", "native-other"],
            Entries(await Send("getPlaylist", $"id={virtualId}")));
        backend.Reverse();
        await Send("updatePlaylist", "playlistId=native-playlist&songIndexToRemove=4");
        Assert.Equal(["native-song"], backend);
        var callsBeforeRead = gateway.Invocations.Count;
        var snapshotsBeforeRead = await MembershipSnapshotCountAsync(factory);
        var visible = await Send("getPlaylist", "id=native-playlist");
        Assert.Equal(callsBeforeRead, gateway.Invocations.Count);
        Assert.Equal(snapshotsBeforeRead, await MembershipSnapshotCountAsync(factory));
        owner = "other";
        using (var denied = await client.GetAsync($"/rest/updatePlaylist.view?{authentication}&playlistId=native-playlist&songIdToAdd=ext-deezer-song-B"))
        {
            var text = await denied.Content.ReadAsStringAsync();
            if (format == "json") Assert.Equal(50, JsonNode.Parse(text)!["subsonic-response"]!["error"]!["code"]!.GetValue<int>());
            else Assert.Equal("50", XDocument.Parse(text).Descendants().Single(item => item.Name.LocalName == "error").Attribute("code")!.Value);
        }
        Assert.Equal(callsBeforeRead, gateway.Invocations.Count);
        Assert.Equal(snapshotsBeforeRead, await MembershipSnapshotCountAsync(factory));
        owner = "fixture";
        await Send("deletePlaylist", $"id={virtualId}");
        backend.Add("native-new");
        Assert.Equal(["native-new"], Entries(await Send("getPlaylist", "id=native-playlist")));
        Assert.NotEmpty(visible);
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.AtLeastOnce);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Feishin")]
    [InlineData("Finamp")]
    [InlineData("Web")]
    public async Task JellyfinMixedPlaylist_PersistsDistinctOccurrencesAcrossRemoveMoveAndReplace(string clientName)
    {
        var backend = new List<(string Id, string Entry)>();
        var sequence = 0;
        var writable = true;
        var isPublic = true;
        static string NativeRow((string Id, string Entry) row) =>
            $$"""{ "Id": "{{row.Id}}", "PlaylistItemId": "{{row.Entry}}", "Name": "Native", "Type": "Audio", "Future": { "Keep": true } }""";
        var gateway = MixedPlaylistGateway();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1","Name":"Owner"}""");
            if (path == "/Playlists" && request.Method == HttpMethod.Post)
            {
                var dto = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                Assert.Equal("Mixed", dto["Name"]!.GetValue<string>());
                Assert.True(dto["IsPublic"]!.GetValue<bool>());
                backend = dto["Ids"]!.AsArray().Select(id => (id!.GetValue<string>(), $"{++sequence:x32}")).ToList();
                Assert.DoesNotContain(backend, row => row.Id.StartsWith("ext-", StringComparison.Ordinal));
                return Json(200, """{"Id":"4383a46d8bcac3be2ef9385053ea18df"}""");
            }
            if (path == "/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items" && request.Method == HttpMethod.Post)
            {
                if (!writable) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                foreach (var id in query.GetValueOrDefault("Ids").ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    Assert.False(id.StartsWith("ext-", StringComparison.Ordinal));
                    backend.Add((id, $"{++sequence:x32}"));
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == "/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items" && request.Method == HttpMethod.Delete)
            {
                var removed = query["EntryIds"].ToString().Split(',');
                backend.RemoveAll(row => removed.Contains(row.Entry));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path.Contains("/Move/", StringComparison.Ordinal))
            {
                var parts = path.Split('/');
                var entry = backend.Single(row => row.Entry == parts[^3]);
                backend.Remove(entry);
                backend.Insert(int.Parse(parts[^1]), entry);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == "/Playlists/4383a46d8bcac3be2ef9385053ea18df" && request.Method == HttpMethod.Post)
            {
                var dto = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                if (dto["IsPublic"] is JsonValue visibility) isPublic = visibility.GetValue<bool>();
                if (dto["Ids"] is JsonArray ids) backend = ids.Select(id => (id!.GetValue<string>(), $"{++sequence:x32}")).ToList();
                Assert.DoesNotContain(backend, row => row.Id.StartsWith("ext-", StringComparison.Ordinal));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == "/Playlists/4383a46d8bcac3be2ef9385053ea18df") return Json(200, JsonSerializer.Serialize(new
            { OpenAccess = isPublic, ItemIds = Array.Empty<string>(), Shares = new[] { new { UserId = "user-1", CanEdit = writable } } }));
            return Json(200, $$"""{"Items":[{{string.Join(',', backend.Select(NativeRow))}}],"TotalRecordCount":{{backend.Count}},"StartIndex":0}""");
        }, services => ConfigureMixedPlaylists(services, gateway, "4383a46d8bcac3be2ef9385053ea18df"));
        using var client = factory.CreateClient();
        await SeedMixedPlaylistUserAsync(factory, "jellyfin", "user-1");
        using (var definition = await client.GetAsync("/Playlists/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0?api_key=fixture-key"))
        {
            Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
            var dto = JsonNode.Parse(await definition.Content.ReadAsStringAsync())!;
            Assert.True(dto["OpenAccess"]!.GetValue<bool>());
            Assert.True(Assert.Single(dto["Shares"]!.AsArray())!["CanEdit"]!.GetValue<bool>());
        }
        async Task<JsonObject[]> Read()
        {
            using var response = await client.GetAsync("/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items?api_key=fixture-key&UserId=user-1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            using var nativeCheck = JsonDocument.Parse(text);
            foreach (var row in backend)
            {
                var item = nativeCheck.RootElement.GetProperty("Items").EnumerateArray()
                    .Single(item => item.GetProperty("PlaylistItemId").GetString() == row.Entry);
                Assert.Equal(NativeRow(row), item.GetRawText());
            }
            return JsonNode.Parse(text)!["Items"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        }
        using var created = await client.PostAsync("/Playlists?api_key=fixture-key", new StringContent(
            """{"Name":"Mixed","IsPublic":true,"UserId":"user-1","Ids":["4383a46d8bcac3be2ef9385053ea18aa","ext-deezer-song-A","ext-deezer-song-A","ext-deezer-song-B"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("4383a46d8bcac3be2ef9385053ea18df", JsonNode.Parse(await created.Content.ReadAsStringAsync())!["Id"]!.GetValue<string>());
        var first = await Read();
        Assert.Equal(["4383a46d8bcac3be2ef9385053ea18aa", "ext-deezer-song-A", "ext-deezer-song-A", "ext-deezer-song-B"], first.Select(item => item["Id"]!.GetValue<string>()));
        var entries = first.Select(item => item["PlaylistItemId"]!.GetValue<string>()).ToArray();
        Assert.Equal(4, entries.Distinct().Count());
        var entryParameter = clientName == "Finamp" ? "entryIds" : "EntryIds";
        using var removed = await client.DeleteAsync($"/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items?api_key=fixture-key&{entryParameter}={entries[2]}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var moved = await client.PostAsync($"/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items/{entries[3]}/Move/0?api_key=fixture-key", null);
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        var after = await Read();
        Assert.Equal(["ext-deezer-song-B", "4383a46d8bcac3be2ef9385053ea18aa", "ext-deezer-song-A"], after.Select(item => item["Id"]!.GetValue<string>()));
        Assert.Equal([entries[3], entries[0], entries[1]], after.Select(item => item["PlaylistItemId"]!.GetValue<string>()));
        using var replaced = await client.PostAsync("/Playlists/4383a46d8bcac3be2ef9385053ea18df?api_key=fixture-key", new StringContent(
            """{"Ids":["ext-deezer-song-A","4383a46d8bcac3be2ef9385053ea18aa","ext-deezer-song-B"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        var afterReplacement = await Read();
        Assert.Equal(["ext-deezer-song-A", "4383a46d8bcac3be2ef9385053ea18aa", "ext-deezer-song-B"], afterReplacement.Select(item => item["Id"]!.GetValue<string>()));
        Assert.Equal(entries[1], afterReplacement[0]["PlaylistItemId"]!.GetValue<string>());
        Assert.Equal(entries[3], afterReplacement[2]["PlaylistItemId"]!.GetValue<string>());
        using var added = await client.PostAsync("/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items?api_key=fixture-key&Ids=ext-deezer-song-A,4383a46d8bcac3be2ef9385053ea18bb&UserId=user-1", null);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        Assert.Equal(5, (await Read()).Length);
        using var metadata = await client.PostAsync("/Playlists/4383a46d8bcac3be2ef9385053ea18df?api_key=fixture-key", new StringContent(
            """{"Name":"Renamed","Ids":null,"IsPublic":false}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, metadata.StatusCode);
        Assert.Equal(5, (await Read()).Length);
        using (var definition = await client.GetAsync("/Playlists/4383a46d8bcac3be2ef9385053ea18df?api_key=fixture-key"))
        {
            var dto = JsonNode.Parse(await definition.Content.ReadAsStringAsync())!;
            Assert.False(dto["OpenAccess"]!.GetValue<bool>());
            Assert.Equal(5, dto["ItemIds"]!.AsArray().Count);
        }
        var callsBeforeRead = gateway.Invocations.Count;
        var snapshotsBeforeRead = await MembershipSnapshotCountAsync(factory);
        await Read();
        Assert.Equal(callsBeforeRead, gateway.Invocations.Count);
        Assert.Equal(snapshotsBeforeRead, await MembershipSnapshotCountAsync(factory));
        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,
                   "/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items?api_key=fixture-key&UserId=user-1")))
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(callsBeforeRead, gateway.Invocations.Count);
        Assert.Equal(snapshotsBeforeRead, await MembershipSnapshotCountAsync(factory));
        writable = false;
        using var denied = await client.PostAsync("/Playlists/4383a46d8bcac3be2ef9385053ea18df/Items?api_key=fixture-key&Ids=ext-deezer-song-B&UserId=user-1", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(callsBeforeRead, gateway.Invocations.Count);
        Assert.Equal(snapshotsBeforeRead, await MembershipSnapshotCountAsync(factory));
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()), Times.AtLeastOnce);
        gateway.VerifyNoOtherCalls();
        Assert.False(string.IsNullOrWhiteSpace(clientName));
    }

    [Theory]
    [InlineData("createPlaylist", "playlistId")]
    [InlineData("deletePlaylist", "id")]
    public async Task SubsonicWritablePlaylist_ReplacementAndDeletionUseNativeTarget(string method, string key)
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            observed.Add(Observe(request));
            return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        }, services =>
        {
            services.RemoveAll<ISubsonicPlaylistMutationResolver>();
            services.AddSingleton<ISubsonicPlaylistMutationResolver>(new FixedPlaylistMutationResolver(new(true, "native-target")));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/{method}.view?u=fixture&p=secret&v=1.16.1&c=Feishin&f=json&{key}=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0&songId=native");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, observed.Count);
        Assert.Contains($"{key}=native-target", observed[1].PathAndQuery);
        Assert.DoesNotContain("allstarr-vpl-", observed[1].PathAndQuery);
    }

    [Fact]
    public async Task JellyfinWritablePlaylist_DeletionUsesNativeTarget()
    {
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            observed.Add(Observe(request));
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.Method == HttpMethod.Get) return Json(200, """{"Id":"4383a46d8bcac3be2ef9385053ea18df","Type":"Playlist"}""");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }, services =>
        {
            services.RemoveAll<IJellyfinPlaylistMutationResolver>();
            services.AddSingleton<IJellyfinPlaylistMutationResolver>(new FixedJellyfinPlaylistMutationResolver(new(true, "4383a46d8bcac3be2ef9385053ea18df")));
        });
        using var client = factory.CreateClient();
        using var response = await client.DeleteAsync("/Items/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var mutation = Assert.Single(observed, item => item.Method == "DELETE");
        Assert.StartsWith("/Items/4383a46d8bcac3be2ef9385053ea18df?", mutation.PathAndQuery, StringComparison.Ordinal);
    }

    private static Mock<IProtocolProviderGateway> MixedPlaylistGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => new Song
            { Id = $"ext-deezer-song-{id}", ExternalProvider = "deezer", ExternalId = id, Title = $"Track {id}", Artist = "Artist", Album = "Album", AlbumId = "ext-deezer-album-album", Duration = 180 });
        return gateway;
    }

    private static void ConfigureMixedPlaylists(IServiceCollection services, Mock<IProtocolProviderGateway> gateway, string targetId = "native-playlist")
    {
        services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway.Object);
        services.RemoveAll<ILibraryIndexService>();
        services.AddSingleton(Mock.Of<ILibraryIndexService>());
        var virtualId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        var model = new VirtualPlaylistReadModel(virtualId, Guid.NewGuid(), Guid.NewGuid(), "Mixed", null, null,
            "spotify", "source", "revision", PlaylistLinkMode.Materialized, [], TargetPlaylistId: targetId);
        var virtualization = new Mock<IPlaylistVirtualizationService>();
        virtualization.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), virtualId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        services.RemoveAll<IPlaylistVirtualizationService>();
        services.AddSingleton(virtualization.Object);
        services.RemoveAll<ISubsonicPlaylistMutationResolver>();
        services.AddSingleton<ISubsonicPlaylistMutationResolver>(new FixedPlaylistMutationResolver(new(true, targetId)));
        services.RemoveAll<IJellyfinPlaylistMutationResolver>();
        services.AddSingleton<IJellyfinPlaylistMutationResolver>(new FixedJellyfinPlaylistMutationResolver(new(true, targetId)));
        var policies = new Mock<allstarr.Core.Settings.IEffectiveProviderPolicyResolver>();
        policies.Setup(service => service.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new allstarr.Core.Settings.EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { UserId = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd") });
        policies.Setup(service => service.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new allstarr.Core.Settings.EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07));
        services.RemoveAll<allstarr.Core.Settings.IEffectiveProviderPolicyResolver>();
        services.AddSingleton(policies.Object);
    }

    private static async Task<int> MembershipSnapshotCountAsync(ProtocolFactory factory)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        return await db.ExternalMetadataSnapshots.CountAsync();
    }

    private static async Task SeedMixedPlaylistUserAsync(ProtocolFactory factory, string protocol, string principal)
    {
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Users.Add(new UserRecord
        {
            Id = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd"),
            BackendType = protocol,
            BackendInstanceId = "primary",
            BackendPrincipalId = principal,
            DisplayName = "Owner",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
