using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ExternalAlbumReadsKeepFieldsOrderAndImageTagsWithALargeSavedLibrary()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "timing"))
            .ReturnsAsync(() => new Album
            {
                Id = "ext-deezer-album-timing",
                Title = "Album",
                Artist = "Artist",
                ExternalProvider = "deezer",
                ExternalId = "timing",
                Songs = [new() { Id = "ext-deezer-song-second", Title = "Second", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "second" },
                    new() { Id = "ext-deezer-song-first", Title = "First", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "first" }]
            });
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        var paths = new[] { "/Items/ext-deezer-album-timing", "/Items?ParentId=ext-deezer-album-timing&Fields=DateCreated,ParentId&Limit=200" };
        var expected = new List<string>();
        foreach (var path in paths) expected.Add(await client.GetStringAsync(path));
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            for (var index = 0; index < 180; index++)
            {
                var id = $"ext-deezer-album-saved-{index}";
                var eventId = Guid.NewGuid();
                var jobId = Guid.NewGuid();
                db.Set<DurableJobRecord>().Add(new()
                {
                    Id = jobId,
                    OwnerUserId = owner,
                    Type = "favorite.process",
                    ScopeKey = id,
                    IdempotencyKey = id,
                    RequestFingerprint = id,
                    CorrelationId = id,
                    Revision = 1
                });
                var display = JsonSerializer.Serialize(new FavoriteDisplaySnapshot(1, id, "album", $"Saved album {index}", [$"Artist {index}"]), FavoriteDisplaySnapshot.JsonOptions);
                db.FavoriteEvents.Add(new()
                {
                    Id = eventId,
                    OwnerUserId = owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendPrincipalId = "listener",
                    ItemId = id,
                    JobId = jobId,
                    Operation = FavoriteOperation.Favorite,
                    EventKey = eventId.ToString(),
                    CreatedAt = DateTimeOffset.UnixEpoch
                });
                db.FavoriteStates.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    ItemId = id,
                    IsFavorite = true,
                    LastEventId = eventId,
                    DisplayMetadataJson = display
                });
            }
            await db.SaveChangesAsync();
        }
        for (var index = 0; index < paths.Length; index++)
        {
            foreach (var state in new[] { "first", "warm" })
            {
                using var response = await client.GetAsync(paths[index]);
                response.EnsureSuccessStatusCode();
                Assert.Equal(expected[index], await response.Content.ReadAsStringAsync());
                AssertTimingStages(response, "merge");
                _lyricsSearchOutput.WriteLine($"External album {index} {state}: " + Assert.Single(response.Headers.GetValues("Server-Timing")));
            }
        }
    }
    [Fact]
    public async Task ExternalAlbumSavedProjectionPreservesReleaseChoicesAndEditionLabels()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        var user = FeedReadsTests.User("listener");
        var context = FeedReadsTests.Context(user);
        var displays = new[]
        {
            new FavoriteDisplaySnapshot(1, "ext-deezer-album-original", "album", "Album", ["Artist"]) { Upc = "123456789012", IsExplicit = true },
            new FavoriteDisplaySnapshot(1, "ext-tidal-album-copy", "album", "Album", ["Artist"]) { Upc = "123456789012", IsExplicit = true },
            new FavoriteDisplaySnapshot(1, "ext-qobuz-album-clean", "album", "Album", ["Artist"]) { Upc = "123456789013", IsExplicit = false },
            new FavoriteDisplaySnapshot(1, "ext-deezer-album-deluxe", "album", "Album", ["Artist"]) { Edition = "Deluxe", Upc = "123456789014", IsExplicit = true },
            new FavoriteDisplaySnapshot(1, "ext-deezer-album-unrelated", "album", "Album", ["Other artist"]) { IsExplicit = true }
        };
        await using (var db = storage.CreateDbContext())
        {
            db.Users.Add(user);
            for (var index = 0; index < displays.Length; index++)
            {
                var display = displays[index];
                var eventId = Guid.NewGuid();
                var jobId = Guid.NewGuid();
                db.Set<DurableJobRecord>().Add(new()
                {
                    Id = jobId,
                    OwnerUserId = user.Id,
                    Type = "favorite.process",
                    ScopeKey = display.ItemId,
                    IdempotencyKey = display.ItemId,
                    RequestFingerprint = display.ItemId,
                    CorrelationId = display.ItemId,
                    Revision = 1
                });
                db.FavoriteEvents.Add(new()
                {
                    Id = eventId,
                    OwnerUserId = user.Id,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendPrincipalId = "listener",
                    ItemId = display.ItemId,
                    JobId = jobId,
                    Operation = FavoriteOperation.Favorite,
                    EventKey = eventId.ToString(),
                    CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(index)
                });
                db.FavoriteStates.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = user.Id,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    ItemId = display.ItemId,
                    IsFavorite = true,
                    LastEventId = eventId,
                    DisplayMetadataJson = JsonSerializer.Serialize(display, FavoriteDisplaySnapshot.JsonOptions)
                });
            }
            await db.SaveChangesAsync();
        }
        var expected = new[] { ("ext-deezer-album-deluxe", "Album (Deluxe)"), ("ext-deezer-album-original", "Album (Explicit)"),
            ("ext-deezer-album-unrelated", "Album"), ("ext-qobuz-album-clean", "Album (Clean)") };
        foreach (var read in new[] { await new FavoriteViewReader(storage).ReadAsync(context), await new SavedLibraryReader(storage).ReadAsync(context) })
            Assert.Equal(expected, read.OrderBy(item => item.ItemId, StringComparer.Ordinal).Select(item => (item.ItemId, item.Display.Title)));
    }

    [Fact]
    public async Task ExternalAlbumSavedDatesReuseTheRequestFavoriteView()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var reads = new AppearanceReads();
        var storage = new FavoriteViewFactory(new DbContextOptionsBuilder<AllstarrDbContext>(database.Options).AddInterceptors(reads).Options);
        var user = FeedReadsTests.User("listener");
        await using (var db = storage.CreateDbContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        var execution = FeedReadsTests.Context(user);
        var saved = new SavedLibraryReader(storage);
        reads.Count = 0;
        var expected = await saved.ReadAsync(execution);
        var baselineReads = reads.Count;
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = execution;
        reads.Count = 0;
        await new FavoriteViewReader(storage).ReadForRequestAsync(http);
        var favoriteReads = reads.Count;
        reads.Count = 0;
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await saved.ReadForRequestAsync(http)));
        Assert.Equal(baselineReads - favoriteReads, reads.Count);
        _lyricsSearchOutput.WriteLine($"Saved dates: database reads {baselineReads} -> {reads.Count}; reused {favoriteReads} favorite reads.");
    }

}
