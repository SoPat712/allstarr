using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("starred", "", "ext-deezer-album-saved")]
    public async Task SubsonicStarredAlbumsKeepHeartedServiceAlbums(string mode, string filter, string expected)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSavedBrowseAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
            : Json(200, """{"subsonic-response":{"status":"ok","albumList2":{"album":[]}}}"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/getAlbumList2?u=fixture&p=secret&v=1.13.0&c=Feishin&f=json&type={mode}&size=20{filter}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, body, StringComparison.Ordinal);
        if (mode == "starred") Assert.DoesNotContain("ext-deezer-album-related", body, StringComparison.Ordinal);
        gateway.VerifyNoOtherCalls();
    }

    private static async Task SeedSavedBrowseAsync(FavoriteViewFactory storage)
    {
        await SeedFavoriteViewsAsync(storage, "subsonic");
        await using var db = storage.CreateDbContext();
        var snapshot = new FavoriteDisplaySnapshot(1, "ext-deezer-song-saved", "song", "Saved song", ["Related artist"],
            "Related album", 180000, 2003)
        {
            AlbumId = "ext-deezer-album-related",
            ArtistIds = ["ext-deezer-artist-related"],
            AlbumArtistIds = ["ext-deezer-artist-related"],
            Genres = ["Rock"]
        };
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Set<FavoriteStateRecord>().Where(item => item.ItemId == snapshot.ItemId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayMetadataJson, json));
        var added = DateTimeOffset.Parse("2024-02-03T04:05:06Z", System.Globalization.CultureInfo.InvariantCulture);
        await db.Set<FavoriteEventRecord>().Where(item => item.ItemId == snapshot.ItemId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CreatedAt, added));
    }
}
