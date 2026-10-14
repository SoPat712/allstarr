using System.Net;
using System.Text.Json;
using allstarr.Core.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("native-folder")]
    [InlineData("native-playlist")]
    [InlineData("allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0")]
    public async Task JellyfinCatalog_UnknownParentBrowseAddsNoBackendRequests(string parentId)
    {
        var observed = new List<ObservedRequest>();
        const string native = """{"Items":[{"Id":"native-child","Type":"Audio","Future":true}],"TotalRecordCount":1}""";
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            observed.Add(Observe(request));
            if (request.RequestUri.AbsolutePath == "/Items") return Json(200, native);
            return Json(200, """{"Id":"parent","Type":"Folder","Name":"Native folder"}""");
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ParentId={parentId}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var expected = JsonDocument.Parse(native);
        Assert.Equal(CanonicalJson(expected.RootElement), CanonicalJson(body.RootElement));
        var relay = Assert.Single(observed);
        Assert.StartsWith("/Items?", relay.PathAndQuery, StringComparison.Ordinal);
    }

    // Request parameters from Jellyfin Web's itemDetails controller and Finamp issue #1537.
    [Theory]
    [InlineData("album", "ParentId=native-parent&Fields=ItemCounts,PrimaryImageAspectRatio,CanDelete,MediaSourceCount&SortBy=ParentIndexNumber,IndexNumber,SortName")]
    [InlineData("artist", "IncludeItemTypes=MusicAlbum&Recursive=true&ExcludeItemIds=native-parent&SortBy=PremiereDate,ProductionYear,SortName&SortOrder=Descending&ContributingArtistIds=native-parent")]
    [InlineData("album", "IncludeItemTypes=Audio&ParentId=native-parent&Recursive=true&SortBy=ParentIndexNumber%2CIndexNumber%2CSortName&Fields=ChildCount%2CDateCreated%2CDateLastMediaAdded%2CEtag%2CGenres%2CIndexNumber%2CParentId%2CProviderIds%2CTags%2CalbumPrimaryImageTag%2CparentPrimaryImageItemId%2CsongCount")]
    [InlineData("album", "ParentId=native-parent&SortBy=SortName&SortOrder=Ascending&CollapseBoxSetItems=false&ExcludeLocationTypes=Virtual&IsMissing=false")]
    [InlineData("artist", "AlbumArtistIds=native-parent&IncludeItemTypes=MusicAlbum&SortBy=DateCreated&SortOrder=Descending&CollapseBoxSetItems=true&ExcludeLocationTypes=Virtual&IsMissing=false")]
    public async Task JellyfinCatalog_ClientBrowseOptionsKeepNativeOrderThenAppendProviders(string kind, string query)
    {
        var gateway = JellyfinCatalogGateway(kind, false);
        var name = kind == "artist" ? "Visions" : "Oblivion";
        var type = kind == "artist" ? "MusicAlbum" : "Audio";
        var native = $$"""{"Items":[{"Id":"native-z","Name":"{{name}}","Type":"{{type}}","Future":true},{"Id":"native-a","Name":"Owned","Type":"{{type}}"}],"TotalRecordCount":2}""";
        using var factory = JellyfinCatalogFactory(gateway.Object, kind, native, request =>
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query)
                .TryGetValue("ParentId", out var parentId) && parentId == "library"
                ? """{"Items":[{"Id":"native-parent","Type":"MusicAlbum","Name":"Visions"}],"TotalRecordCount":1}"""
                : null);
        using var client = factory.CreateClient();
        if (kind == "album")
        {
            var parentPath = query.StartsWith("IncludeItemTypes=Audio", StringComparison.Ordinal)
                ? "/Users/user-1/Items?ParentId=library&IncludeItemTypes=MusicAlbum&SortBy=SortName&api_key=fixture-key"
                : "/Users/user-1/Items/native-parent?api_key=fixture-key";
            using var parent = await client.GetAsync(parentPath);
            Assert.Equal(HttpStatusCode.OK, parent.StatusCode);
        }
        using var response = await client.GetAsync($"/Users/user-1/Items?{query}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "native-z", "native-a", kind == "artist" ? "ext-deezer-album-missing" : "ext-deezer-song-missing" }, rows.Select(row => row.GetProperty("Id").GetString()));
        Assert.True(rows[0].GetProperty("Future").GetBoolean());
        Assert.Equal(3, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        gateway.VerifyAll();
    }

    [Fact]
    public async Task JellyfinCatalog_BrowseRemembersOnlyAlbumTypes()
    {
        var cache = new TestMemoryApplicationCache();
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1"}""")
                : Json(200, """{"Items":[{"Id":"album","Type":"MusicAlbum"},{"Id":"artist","Type":"MusicArtist"},{"Id":"song","Type":"Audio"},{"Id":"folder","Type":"Folder"},{"Id":"playlist","Type":"Playlist"}],"TotalRecordCount":5}"""),
            services =>
            {
                services.RemoveAll<allstarr.Services.Common.IApplicationCache>();
                services.AddSingleton<allstarr.Services.Common.IApplicationCache>(cache);
            });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?Recursive=true&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = Assert.Single(cache.GetKeysByPattern("jellyfin:catalog-type:*"));
        Assert.EndsWith(":album", key);
        Assert.Equal("MusicAlbum", await cache.GetStringAsync(key));
    }

    [Theory]
    [InlineData("Filters=IsPlayed")]
    [InlineData("IsPlayed=false")]
    [InlineData("Genres=Rock")]
    [InlineData("GenreIds=genre-1")]
    [InlineData("Years=2020")]
    [InlineData("Tags=Favorite")]
    [InlineData("IsMissing=true")]
    [InlineData("ExcludeItemIds=native-child")]
    public async Task JellyfinCatalog_FilteredViewsPreserveNativeResponseAndMakeNoDetailLookup(string filter)
    {
        foreach (var kind in new[] { "artist", "album" })
        {
            var gateway = JellyfinCatalogGateway(kind, false);
            var observed = new List<ObservedRequest>();
            var native = kind == "artist"
                ? """{ "Items":[{"Id":"native-child","Type":"MusicAlbum","Name":"Visions"}], "TotalRecordCount":1 }"""
                : """{ "Items":[{"Id":"native-child","Type":"Audio","Name":"Oblivion"}], "TotalRecordCount":1 }""";
            using var factory = new ProtocolFactory("Jellyfin", request =>
            {
                if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
                observed.Add(Observe(request));
                if (request.RequestUri.AbsolutePath == "/Items") return Json(200, native);
                return Json(200, kind == "artist"
                    ? """{"Id":"native-parent","Type":"MusicArtist","Name":"Grimes"}"""
                    : """{"Id":"native-parent","Type":"MusicAlbum","Name":"Visions","AlbumArtist":"Grimes"}""");
            }, services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
            using var client = factory.CreateClient();
            using var parent = await client.GetAsync("/Users/user-1/Items/native-parent?api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, parent.StatusCode);
            observed.Clear();
            var relation = kind == "artist" ? "ArtistIds=native-parent&IncludeItemTypes=MusicAlbum" : "ParentId=native-parent";
            using var response = await client.GetAsync($"/Items?{relation}&{filter}&api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(native, await response.Content.ReadAsStringAsync());
            Assert.Single(observed);
            gateway.VerifyNoOtherCalls();
        }
    }
}
