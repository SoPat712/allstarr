using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Storage;
using allstarr.Core.Operations;
using allstarr.Services.Common;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class VirtualPlaylistProtocolAdapterTests
{
    [Fact]
    public async Task ServedPlaylistPagesReuseResolvedTracksAndInvalidateAfterStoredChanges()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ReadCacheFactory(database.Options);
        var model = Model() with
        {
            Tracks = Enumerable.Range(0, 200).Select(index => Model().Tracks[2] with
            {
                SourcePosition = index,
                BackendItemId = "ext-deezer-song-" + index % 4,
                RouteKind = allstarr.Core.Matching.TrackRouteKind.External,
                RouteProviderId = "deezer",
                RouteExternalId = (index % 4).ToString()
            }).ToArray()
        };
        var reads = 0;
        var service = new Mock<IPlaylistVirtualizationService>();
        service.Setup(item => item.ReadAsync(It.IsAny<ProtocolExecutionContext>(), ProtocolId, It.IsAny<CancellationToken>()))
            .Returns(async () => { reads++; await Task.Delay(150); return model; });
        var providerReads = 0;
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns(async (ProtocolExecutionContext _, string _, string id) =>
            {
                Interlocked.Increment(ref providerReads);
                await Task.Delay(800);
                return new Song
                {
                    Id = "ext-deezer-song-" + id,
                    Title = "Song " + id,
                    Artist = "Artist",
                    Album = "Album",
                    ExternalProvider = "deezer",
                    ExternalId = id,
                    Duration = 180,
                    PreciseDuration = TimeSpan.FromMilliseconds(180123)
                };
            });
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(service.Object, new StubJellyfinMutationResolver(null),
            responseBuilder: new JellyfinResponseBuilder(), providerGateway: gateway.Object, readContexts: factory, readCache: cache);
        var uncached = new JellyfinVirtualPlaylistProtocolAdapter(service.Object, new StubJellyfinMutationResolver(null),
            responseBuilder: new JellyfinResponseBuilder(), providerGateway: gateway.Object);
        var user = FeedReadsTests.User("principal");
        user.BackendInstanceId = "backend";
        await using (var seed = factory.CreateDbContext())
        {
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }
        var context = FeedReadsTests.Context(user);
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["Limit"] = "50" });
        var watch = Stopwatch.StartNew();
        var before = JsonSerializer.Serialize(Assert.IsType<JsonResult>(await uncached.ReadItemsAsync(context, ProtocolId, null, query, default)).Value);
        var baseline = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(1, reads);
        Assert.Equal(4, providerReads);
        reads = 0; providerReads = 0;
        watch.Restart();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => adapter.ReadItemsAsync(context, ProtocolId, null, query, default)));
        var first = JsonSerializer.Serialize(Assert.IsType<JsonResult>(concurrent[0]).Value);
        Assert.All(concurrent, result => Assert.Equal(first, JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value)));
        var cold = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var repeated = JsonSerializer.Serialize(Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(context, ProtocolId, null, query, default)).Value);
        var warm = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(first, repeated);
        Assert.Equal(before, first);
        Assert.Equal(1, reads);
        Assert.Equal(4, providerReads);
        query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["StartIndex"] = "50", ["Limit"] = "50" });
        var page = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(context, ProtocolId, null, query, default));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page.Value));
        Assert.Equal(200, json.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(50, json.RootElement.GetProperty("Items").GetArrayLength());
        Assert.Equal(1, reads);
        Assert.Equal(4, providerReads);
        await using var db = factory.CreateDbContext();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.ExternalMetadataSnapshots.Add(FeedReadsTests.Snapshot(user.Id, "personal-mixes"));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await adapter.ReadItemsAsync(context, ProtocolId, null, query, default);
        Assert.Equal(2, reads);
        Assert.Equal(8, providerReads);
        _output.WriteLine($"Served playlist 200 tracks / 50 per page: uncached {baseline:F1} ms; first {cold:F1} ms; warm {warm:F1} ms; resolved-list reads 1 -> 0 and provider reads 4 -> 0 on repeat and next page; response bytes equal.");
    }

    [Fact]
    public async Task ServedPlaylistSummariesInvalidateOnFeedSyncRemovalAndMixRefresh()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new ReadCacheFactory(database.Options);
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var reads = 0;
        var model = Model() with { Tracks = [], SummaryTrackCount = 200, SummaryDurationMilliseconds = 36000000 };
        var service = new Mock<IPlaylistVirtualizationService>();
        service.Setup(item => item.ListAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(() => { reads++; return Task.FromResult<IReadOnlyList<VirtualPlaylistReadModel>>(Enumerable.Range(0, 36).Select(_ => model).ToArray()); });
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(service.Object, new StubJellyfinMutationResolver(null), readContexts: factory, readCache: cache);
        var user = FeedReadsTests.User("principal");
        user.BackendInstanceId = "backend";
        await using (var seed = factory.CreateDbContext())
        {
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }
        var context = FeedReadsTests.Context(user);
        var first = await adapter.ListAsync(context, default);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await adapter.ListAsync(context, default)));
        Assert.Equal(1, reads);
        await using var db = factory.CreateDbContext();
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            SourceProviderId = "spotify",
            SourcePlaylistId = "feed",
            SourcePlaylistIdHash = new string('a', 64),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "backend"
        };
        db.PlaylistLinks.Add(link);
        await db.SaveChangesAsync();
        await adapter.ListAsync(context, default);
        Assert.Equal(2, reads);
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            PlaylistLinkId = link.Id,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            Name = "Feed",
            PayloadSha256 = new string('b', 64),
            CorrelationId = "fixture"
        });
        await db.SaveChangesAsync();
        await adapter.ListAsync(context, default);
        Assert.Equal(3, reads);
        link.Enabled = false;
        await db.SaveChangesAsync();
        await adapter.ListAsync(context, default);
        Assert.Equal(4, reads);
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            ResourceKind = "personal-mixes",
            ProviderId = "allstarr",
            ExternalIdHash = new string('c', 64),
            SnapshotVersion = 1,
            PayloadSha256 = new string('d', 64),
            CorrelationId = "fixture"
        });
        await db.SaveChangesAsync();
        await adapter.ListAsync(context, default);
        Assert.Equal(5, reads);
        user.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await adapter.ListAsync(context, default);
        Assert.Equal(5, reads);
    }

    private sealed class ReadCacheFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
