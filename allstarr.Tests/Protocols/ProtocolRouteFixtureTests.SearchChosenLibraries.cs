using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private static readonly Song ChosenLibrariesServiceSong = new()
    {
        Id = "ext-deezer-song-copy",
        Title = "Anything copy",
        Artist = "Someone",
        Album = "Album",
        Duration = 200,
        Isrc = "GBAHT2100009",
        ExternalProvider = "deezer",
        ExternalId = "copy"
    };

    [Fact]
    public async Task JellyfinSearch_LibraryResultsFollowTheChosenLibrariesWithinTheAppsPick()
    {
        var searches = new ConcurrentQueue<(string Path, string? Library, string Types)>();
        var fetched = new ConcurrentQueue<string>();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, int songs, int _, int _, string? _) =>
                new SearchResult { Songs = songs > 0 ? [ChosenLibrariesServiceSong.Copy()] : [] });
        gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        using var factory = ClientApiFactory(request => ChosenLibrariesJellyfin(request, searches, fetched), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(ChosenLibrariesIndex().Object);
        });
        using var client = CreateClientApiClient(factory);
        await SeedChosenLibrariesCopyAsync(factory, "Jellyfin", "jellyfin");
        async Task<string> GetAsync(string path)
        {
            searches.Clear();
            fetched.Clear();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Emby-Token", "listener-token");
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            return body;
        }

        // Without a choice every library is searched at once, and the listener's own copy stands in for the service song.
        var all = await GetAsync("/Items?SearchTerm=anything&IncludeItemTypes=Audio&Limit=20");
        Assert.Null(Assert.Single(searches).Library);
        Assert.Contains("kids-copy", all);
        Assert.Contains(fetched, ids => ids.Contains("kids-copy"));

        using (var choose = new HttpRequestMessage(HttpMethod.Patch, "/allstarr/v2/me/preferences"))
        {
            choose.Content = JsonContent.Create(new { libraries = new { selected = new[] { "mine" } } });
            choose.Headers.Authorization = new("Bearer", "listener-token");
            using var chosen = await client.SendAsync(choose);
            Assert.True(chosen.IsSuccessStatusCode, await chosen.Content.ReadAsStringAsync());
        }

        // The chosen library is searched on its own, and a copy kept in a library left out never replaces the service song.
        var narrowed = await GetAsync("/Items?SearchTerm=anything&IncludeItemTypes=Audio&Limit=20");
        Assert.Equal("mine", Assert.Single(searches).Library);
        Assert.Contains("item-mine", narrowed);
        Assert.Contains(ChosenLibrariesServiceSong.Id, narrowed);
        Assert.DoesNotContain("kids-copy", narrowed);
        Assert.DoesNotContain(fetched, ids => ids.Contains("kids-copy"));

        // An app that asks for a chosen library is asked as it asked; one that asks for a library left out finds no library songs.
        await GetAsync("/Items?SearchTerm=anything&IncludeItemTypes=Audio&ParentId=mine&Limit=20");
        Assert.Equal("mine", Assert.Single(searches).Library);
        var left = await GetAsync("/Items?SearchTerm=anything&IncludeItemTypes=Audio&ParentId=kids&Limit=20");
        Assert.Empty(searches);
        Assert.DoesNotContain("item-", left);
        Assert.DoesNotContain("kids-copy", left);
        Assert.Contains(ChosenLibrariesServiceSong.Id, left);

        var hints = await GetAsync("/Search/Hints?SearchTerm=anything&Limit=20");
        Assert.Equal("mine", Assert.Single(searches).Library);
        Assert.Contains("item-mine", hints);
        Assert.DoesNotContain("kids-copy", hints);

        var artists = await GetAsync("/Artists?SearchTerm=anything&Limit=20");
        Assert.Equal("mine", Assert.Single(searches).Library);
        Assert.Contains("item-mine", artists);

        // The app's own library-only search is read the same way, with playlists, which belong to no music library,
        // searched on their own, and paged as it asked.
        var scoped = await GetAsync("/Items?SearchTerm=anything&allstarrScope=library&Limit=1");
        Assert.Equal(new[] { "mine", "(none)" }, searches.Select(search => search.Library ?? "(none)").OrderBy(library => library == "(none)").ToArray());
        using var page = JsonDocument.Parse(scoped);
        Assert.Single(page.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(2, page.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain("allstarrScope", string.Join(' ', searches.Select(search => search.Path)));
    }

    [Fact]
    public async Task ClientApiV2_SearchReadsAServiceSongAsItselfWhenItsCopyIsInALibraryLeftOut()
    {
        var gateway = SearchV2Gateway(new() { Songs = [ChosenLibrariesServiceSong.Copy()] });
        var index = ChosenLibrariesIndex();
        var searches = new ConcurrentQueue<(string Path, string? Library, string Types)>();
        var fetched = new ConcurrentQueue<string>();
        using var factory = ClientDetailsFactory(request => ChosenLibrariesJellyfin(request, searches, fetched), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedChosenLibrariesCopyAsync(factory, "Jellyfin", "jellyfin");
        using (var chosen = await client.PatchAsJsonAsync("/allstarr/v2/me/preferences", new { libraries = new { selected = new[] { "mine" } } }))
            Assert.True(chosen.IsSuccessStatusCode, await chosen.Content.ReadAsStringAsync());

        using var response = await client.GetAsync("/allstarr/v2/search?q=anything&types=songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var final = SearchV2Lines(await response.Content.ReadAsStringAsync())[^1];
        var track = final["sections"]!["songs"]!.AsArray().Select(hit => hit!["track"]!)
            .Single(item => item["title"]!.GetValue<string>() == ChosenLibrariesServiceSong.Title);
        Assert.True(track["isExternal"]!.GetValue<bool>());
        Assert.Null(track["libraryItemId"]?.GetValue<string>());
        index.Verify(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SubsonicSearch_LibraryResultsFollowTheChosenFoldersWithinTheAppsPick()
    {
        var folders = new ConcurrentQueue<string?>();
        var fetched = new ConcurrentQueue<string>();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(() => new SearchResult { Songs = [ChosenLibrariesServiceSong.Copy()] });
        gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["mine", "kids"]));
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (path.Contains("ping", StringComparison.Ordinal)) return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (path.Contains("getSong", StringComparison.Ordinal))
            {
                fetched.Enqueue(query["id"].ToString());
                return Json(200, """{"subsonic-response":{"status":"ok","song":{"id":"kids-copy","title":"Anything copy","artist":"Someone","album":"Album","duration":200}}}""");
            }
            if (!path.Contains("search3", StringComparison.Ordinal)) return Json(200, """{"subsonic-response":{"status":"ok"}}""");
            var folder = query.TryGetValue("musicFolderId", out var value) ? value.ToString() : null;
            folders.Enqueue(folder);
            return Json(200, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["subsonic-response"] = new Dictionary<string, object>
                {
                    ["status"] = "ok",
                    ["searchResult3"] = new { song = new[] { new { id = "item-" + (folder ?? "all"), title = "Anything", artist = "Someone" } } }
                }
            }));
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(ChosenLibrariesIndex().Object);
            // Only the listener's libraries come from here; the rest of the app keeps its own access.
            services.AddSingleton(access.Object);
        }, new Dictionary<string, string?> { ["Subsonic:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        // A choice belongs to a linked listener, so this search runs with storage ready.
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Subsonic", "listener", "listener")))!.UserId;
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().UpdateMyLibrariesAsync(owner, ["mine"]);
        await SeedChosenLibrariesCopyAsync(factory, "Subsonic", "subsonic");
        async Task<JsonElement> SearchAsync(string extra)
        {
            while (folders.TryDequeue(out _)) { }
            while (fetched.TryDequeue(out _)) { }
            using var response = await client.GetAsync("/rest/search3?u=listener&p=secret&v=1.16.1&c=fixture&f=json&query=anything&songCount=20&albumCount=0&artistCount=0" + extra);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song").Clone();
        }
        string[] Ids(JsonElement songs) => songs.EnumerateArray().Select(song => song.GetProperty("id").GetString()!).ToArray();

        var narrowed = Ids(await SearchAsync(""));
        Assert.Equal("mine", Assert.Single(folders));
        Assert.Contains("item-mine", narrowed);
        Assert.Contains(ChosenLibrariesServiceSong.Id, narrowed);
        Assert.DoesNotContain("kids-copy", narrowed);
        Assert.Empty(fetched);

        Ids(await SearchAsync("&musicFolderId=mine"));
        Assert.Equal("mine", Assert.Single(folders));
        var left = Ids(await SearchAsync("&musicFolderId=kids"));
        Assert.Empty(folders);
        Assert.Equal([ChosenLibrariesServiceSong.Id], left);
    }

    private static HttpResponseMessage ChosenLibrariesJellyfin(HttpRequestMessage request,
        ConcurrentQueue<(string Path, string? Library, string Types)> searches, ConcurrentQueue<string> fetched)
    {
        var path = request.RequestUri!.AbsolutePath;
        switch (path)
        {
            case "/Users/Me": return Json(200, """{"Id":"listener"}""");
            case "/UserViews": return Json(200, """{"Items":[{"Id":"mine","Name":"My music","CollectionType":"music"},{"Id":"kids","Name":"Kids","CollectionType":"music"}]}""");
        }
        var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
        if (query.TryGetValue("Ids", out var ids))
        {
            fetched.Enqueue(ids.ToString());
            return Json(200, ids.ToString().Contains("kids-copy", StringComparison.Ordinal)
                ? """{"Items":[{"Id":"kids-copy","Type":"Audio","Name":"Anything copy","Artists":["Someone"],"Album":"Album","RunTimeTicks":2000000000}],"TotalRecordCount":1}"""
                : """{"Items":[],"TotalRecordCount":0}""");
        }
        if (!query.ContainsKey("SearchTerm")) return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        var library = query.TryGetValue("ParentId", out var parent) ? parent.ToString() : null;
        var types = query.TryGetValue("IncludeItemTypes", out var included) ? included.ToString() : "";
        searches.Enqueue((request.RequestUri.PathAndQuery, library, types));
        var playlist = types == "Playlist";
        var id = (playlist ? "playlist-" : "item-") + (library ?? "all");
        var row = new Dictionary<string, object>
        {
            ["Id"] = id,
            ["ItemId"] = id,
            ["Name"] = "Anything",
            ["Type"] = playlist ? "Playlist" : path.StartsWith("/Artists", StringComparison.Ordinal) ? "MusicArtist" : "Audio",
            ["Artists"] = new[] { "Someone" },
            ["RunTimeTicks"] = 1800000000L
        };
        return Json(200, path.EndsWith("/Search/Hints", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new { SearchHints = new[] { row }, TotalRecordCount = 1 })
            : JsonSerializer.Serialize(new { Items = new[] { row }, TotalRecordCount = 1, StartIndex = 0 }));
    }

    // The service song's only copy is in the "kids" library.
    private static Mock<ILibraryIndexService> ChosenLibrariesIndex()
    {
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext _, IReadOnlyList<LibraryRecordingLookup> lookups, CancellationToken _) =>
                (IReadOnlyDictionary<string, string>)lookups.Where(lookup => lookup.PublicId == ChosenLibrariesServiceSong.Id)
                    .ToDictionary(lookup => lookup.PublicId, _ => "kids-copy"));
        return index;
    }

    private static async Task SeedChosenLibrariesCopyAsync(ProtocolFactory factory, string backend, string protocol)
    {
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new(backend, "listener", "listener")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        db.LibraryTracks.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = protocol,
            BackendInstanceId = "primary",
            BackendLibraryId = "kids",
            BackendItemId = "kids-copy",
            Title = "Anything copy",
            Artist = "Someone"
        });
        await db.SaveChangesAsync();
    }
}
