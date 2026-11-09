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
