using System.Net;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromotionFavorite_StaleExternalIdsUseTheNativeFavoriteRoute(bool favorite)
    {
        const string external = "ext-deezer-song-A";
        var library = new Mock<ILibraryIndexService>();
        library.Setup(item => item.ResolveKnownTrackAsync(It.IsAny<ProtocolExecutionContext>(), external, It.IsAny<CancellationToken>())).ReturnsAsync("native-track");
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"verified-user"}""");
            Assert.Equal("/UserFavoriteItems/native-track", request.RequestUri.AbsolutePath);
            Assert.Equal(favorite ? HttpMethod.Post : HttpMethod.Delete, request.Method);
            return Json(200, """{"Unknown":{"Keep":true,"Value":1.00}}""");
        }, services =>
        {
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(favorite ? HttpMethod.Post : HttpMethod.Delete,
            $"/Users/verified-user/FavoriteItems/{external}?api_key=fixture-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"Unknown":{"Keep":true,"Value":1.00}}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionPlaylistRead_MaterializesNativeCopiesOnceAndPassesThroughTheirCompleteFields(bool readOnly)
    {
        const string playlist = "4383a46d8bcac3be2ef9385053ea18df";
        const string native = "4383a46d8bcac3be2ef9385053ea18aa";
        const string external = "ext-deezer-song-A";
        var members = new List<string>();
        var writes = 0;
        var interrupt = true;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var library = new Mock<ILibraryIndexService>();
        library.Setup(item => item.ResolveKnownTrackAsync(It.IsAny<ProtocolExecutionContext>(), external, It.IsAny<CancellationToken>())).ReturnsAsync(native);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1","Name":"Owner"}""");
            if (path == $"/Users/user-1/Items/{native}")
                return Json(200, $$$"""{"Id":"{{{native}}}","Type":"Audio","Name":"Library title","Artists":["Library artist"],"AlbumId":"native-album","Future":{"Keep":true,"Number":1.00}}""");
            Assert.Equal($"/Playlists/{playlist}/Items", path);
            if (request.Method == HttpMethod.Post)
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                if (!query.ContainsKey("Ids")) return new HttpResponseMessage(readOnly ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent);
                var added = query["Ids"].ToString().Split(',');
                Assert.Equal(new[] { native, native }, added);
                members.AddRange(added);
                writes++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (writes > 0 && interrupt) { interrupt = false; return Json(503, "{}"); }
            var items = members.Select((id, index) => $$$"""{"Id":"{{{id}}}","PlaylistItemId":"entry-{{{index}}}","Type":"Audio","Name":"Library title","Artists":["Library artist"],"AlbumId":"native-album","Future":{"Keep":true,"Number":1.00}}""");
            return Json(200, "{\"Items\":[" + string.Join(',', items) + "],\"TotalRecordCount\":" + members.Count + "}");
        }, services =>
        {
            ConfigureMixedPlaylists(services, gateway, playlist);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
        });
        using var client = factory.CreateClient();
        await SeedMixedPlaylistUserAsync(factory, "jellyfin", "user-1");
        var owner = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "user-1",
            new AllstarrPrincipal(owner, "jellyfin", "primary", "user-1", "Owner", false), "promotion", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var store = factory.Services.GetRequiredService<WritablePlaylistMembership>();
        await store.SaveAsync(context, playlist, [new("first", external, new() { Id = external, Title = "Provider title" }),
            new("second", external, new() { Id = external, Title = "Provider title" })], default);

        if (!readOnly)
        {
            using (var interrupted = await client.GetAsync($"/Playlists/{playlist}/Items?api_key=fixture-key"))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
            Assert.All((await store.ReadAsync(context, playlist, default))!.Members, item => Assert.Equal(external, item.PromotionSourceId));
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync($"/Playlists/{playlist}/Items?api_key=fixture-key");
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(text);
            var items = document.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.All(items, item =>
            {
                Assert.Equal(native, item.GetProperty("Id").GetString());
                Assert.Equal("Library title", item.GetProperty("Name").GetString());
                Assert.True(item.GetProperty("Future").GetProperty("Keep").GetBoolean());
                Assert.Equal("1.00", item.GetProperty("Future").GetProperty("Number").GetRawText());
            });
            Assert.Equal(2, items.Select(item => item.GetProperty("PlaylistItemId").GetString()).Distinct().Count());
            Assert.DoesNotContain("ext-", text);
        }
        Assert.Equal(readOnly ? 0 : 1, writes);
        Assert.All((await store.ReadAsync(context, playlist, default))!.Members, item => Assert.Equal(readOnly, item.IsProvider));
        gateway.VerifyNoOtherCalls();
    }
}
