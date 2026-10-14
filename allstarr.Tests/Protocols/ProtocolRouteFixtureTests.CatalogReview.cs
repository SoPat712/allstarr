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

    [Theory]
    [InlineData("Filters=IsPlayed")]
    [InlineData("IsPlayed=false")]
    [InlineData("Genres=Rock")]
    [InlineData("GenreIds=genre-1")]
    [InlineData("SortBy=DateCreated&SortOrder=Descending")]
    [InlineData("Years=2020")]
    [InlineData("Tags=Favorite")]
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
