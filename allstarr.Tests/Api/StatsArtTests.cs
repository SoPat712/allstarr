using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using System.Threading.Channels;
using allstarr.Controllers.ClientApi;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Search;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed partial class StatsArtTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PreparedArtworkIsReadableWhileDiskPersistenceIsBusy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-recap-contention-{Guid.NewGuid():N}");
        var clock = new SystemPlatformClock();
        using var memory = new MemoryApplicationCache(clock);
        using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), clock, NullLogger<FileMediaApplicationCache>.Instance);
        var writes = new Writes(1);
        var cache = new HybridApplicationCache(memory, disk, clock: clock, writeObserver: writes);
        var gateway = Gateway();
        using var handler = new Covers();
        var reader = new RecapArtworkReader(gateway.Object, new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new Clients(handler));
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        var user = User();
        var gate = (SemaphoreSlim)typeof(FileMediaApplicationCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disk)!;
        await gate.WaitAsync();
        await warmer.StartAsync(default);
        try
        {
            warmer.Queue(user, Recap() with { TopArtists = [], TopAlbums = [] });
            await writes.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var asset = await reader.ReadAsync(Execution(user), "Artist", "Song", null, default);
            Assert.NotNull(asset);
            Assert.True(asset.FromCache);
            Assert.Equal(1, handler.Count);
            gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"), Times.Once);
        }
        finally
        {
            gate.Release();
            await warmer.StopAsync(default);
            await disk.FlushWritesAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BackgroundPreparationWritesTheExactSongAlbumAndArtistVariantsReadByRequests()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var user = User();
        await SeedAsync(factory, user, "Song", DateTimeOffset.UtcNow, cover: true);
        var gateway = Gateway();
        var writes = new Writes(4);
        var cache = new TestMemoryApplicationCache(writes);
        var media = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        using var handler = new Covers();
        var reader = new RecapArtworkReader(gateway.Object, media, new Clients(handler), factory);
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        var recap = Recap() with
        {
            NewArtists = [new("Artist", 1) { Id = "native-artist" }, new("Artist", 1) { Id = "ext-deezer-artist-1" }]
        };
        warmer.Queue(user, recap);
        warmer.Queue(user, recap);
        Assert.Equal(0, handler.Count);
        gateway.VerifyNoOtherCalls();
        await warmer.StartAsync(default);
        try
        {
            await writes.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var protocol = Execution(user);
            var song = await reader.ReadAsync(protocol, "Artist", "Song", null, default);
            var album = await reader.ReadAsync(protocol, "Artist", null, "Album", default);
            var artist = await reader.ReadAsync(protocol, "Artist", null, null, default);
            var native = await reader.ReadAsync(protocol, "Artist", null, null, default, "native-artist");
            Assert.All(new[] { song, album, artist, native }, item => Assert.True(item!.FromCache));
            var expected = await new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance)
                .ResolveAsync(new(user.Id, null, "recap", "recap-song", "baseline", Width: 192),
                    _ => Task.FromResult<MediaAssetSource?>(new(handler.Bytes, "image/png")), 8 * 1024 * 1024);
            Assert.All(new[] { song, album, artist, native }, item => Assert.Equal(expected!.Bytes, item!.Bytes));
            using var decoded = SKBitmap.Decode(song!.Bytes);
            Assert.Equal(192, decoded.Width);
            Assert.Equal(128, decoded.Height);
            Assert.Equal(4, handler.Count);
            gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"), Times.Once);
            gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Album", 0, 8, 0, "deezer"), Times.Once);
            gateway.VerifyNoOtherCalls();
        }
        finally { await warmer.StopAsync(default); }
    }

    [Fact]
    public async Task ComputedAndScheduledRecapsQueueOnlyTheirListenersAndSelectedPeriods()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var clock = new Clock { UtcNow = DateTimeOffset.UtcNow };
        var first = User();
        var second = User();
        await SeedAsync(factory, first, "Recent", clock.UtcNow);
        await SeedAsync(factory, first, "Old", clock.UtcNow.AddDays(-60), addUser: false);
        await SeedAsync(factory, second, "Other", clock.UtcNow);
        var calls = Channel.CreateUnbounded<(Guid? User, string Query)>();
        var gateway = Gateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 8, 0, 0, "deezer"))
            .Returns((ProtocolExecutionContext context, string query, int _, int _, int _, string? _) =>
            {
                Assert.True(context.UseStoredBackendCredentials);
                calls.Writer.TryWrite((context.Principal!.UserId, query));
                return Task.FromResult(new SearchResult { Songs = [new() { Artist = "Artist", Title = query[7..], CoverArtUrl = "https://covers.example.test/song.png" }] });
            });
        using var handler = new Covers();
        var reader = new RecapArtworkReader(gateway.Object,
            new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance), new Clients(handler), factory);
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        using var stats = new ListeningStatsService(factory, clock, artwork: warmer);
        var selected = await stats.GetAsync(Execution(first), "7d", null, default);
        Assert.Equal("Recent", Assert.Single(selected.TopSongs).Title);
        gateway.VerifyNoOtherCalls();
        await warmer.StartAsync(default);
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Equal(((Guid?)first.Id, "Artist Recent"), await calls.Reader.ReadAsync(stop.Token));
            Assert.False(calls.Reader.TryRead(out _));
            await stats.GetAsync(Execution(first), "all", null, default);
            Assert.Equal(((Guid?)first.Id, "Artist Old"), await calls.Reader.ReadAsync(stop.Token));
            await stats.GetAsync(Execution(second), "7d", null, default);
            Assert.Equal(((Guid?)second.Id, "Artist Other"), await calls.Reader.ReadAsync(stop.Token));
            await SeedAsync(factory, first, "Scheduled", clock.UtcNow, addUser: false);
            await stats.WarmAsync(default);
            Assert.Equal(((Guid?)first.Id, "Artist Scheduled"), await calls.Reader.ReadAsync(stop.Token));
            Assert.False(calls.Reader.TryRead(out _));
        }
        finally { await warmer.StopAsync(default); }
    }

    [Fact]
    public async Task BackgroundPreparationRunsAtMostTwoImagesAtOnceAndDeduplicatesPendingImages()
    {
        var started = Channel.CreateUnbounded<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var media = new Mock<IMediaAssetResolver>();
        var active = 0;
        media.Setup(item => item.ResolveAsync(It.IsAny<MediaAssetIdentity>(), It.IsAny<Func<CancellationToken, Task<MediaAssetSource?>>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (MediaAssetIdentity identity, Func<CancellationToken, Task<MediaAssetSource?>> _, int _, CancellationToken token) =>
            {
                Interlocked.Increment(ref active);
                started.Writer.TryWrite(identity.ResourceKind);
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return (ResolvedMediaAsset?)null;
            });
        var reader = new RecapArtworkReader(new Mock<IProtocolProviderGateway>().Object, media.Object, new Mock<IHttpClientFactory>().Object);
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        var user = User();
        warmer.Queue(user, Recap());
        warmer.Queue(user, Recap());
        await warmer.StartAsync(default);
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await started.Reader.ReadAsync(stop.Token);
            await started.Reader.ReadAsync(stop.Token);
            Assert.Equal(2, active);
            Assert.False(started.Reader.TryRead(out _));
            release.SetResult();
            await started.Reader.ReadAsync(stop.Token);
            media.Verify(item => item.ResolveAsync(It.IsAny<MediaAssetIdentity>(), It.IsAny<Func<CancellationToken, Task<MediaAssetSource?>>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        }
        finally { release.TrySetResult(); await warmer.StopAsync(default); }
    }

    [Fact]
    public async Task ConcurrentMissesShareSearchAndDownloadEvenWhenTheFirstWaiterDisconnects()
    {
        var release = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = Gateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"))
            .Returns(release.Task);
        using var handler = new Covers();
        var reader = new RecapArtworkReader(gateway.Object,
            new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance), new Clients(handler));
        var context = Execution(User());
        using var disconnect = new CancellationTokenSource();
        var first = reader.ReadAsync(context, "Artist", "Song", null, disconnect.Token);
        var second = reader.ReadAsync(context, "Artist", "Song", null, default);
        disconnect.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var third = reader.ReadAsync(context, "Artist", "Song", null, default);
        release.SetResult(Found());
        var assets = await Task.WhenAll(second, third);
        Assert.Equal(assets[0]!.Bytes, assets[1]!.Bytes);
        Assert.True((await reader.ReadAsync(context, "Artist", "Song", null, default))!.FromCache);
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"), Times.Once);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task FiveSecondBudgetReturnsTheExistingPlaceholderWithoutCachingItOrStartingFallbackSearch()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var blocked = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<ProtocolExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = Gateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"))
            .Returns((ProtocolExecutionContext context, string _, int _, int _, int _, string? _) => { entered.TrySetResult(context); return blocked.Task; });
        using var handler = new Covers();
        var cache = new TestMemoryApplicationCache();
        var reader = new RecapArtworkReader(gateway.Object, new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance),
            new Clients(handler), time: time);
        var user = User();
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = Execution(user);
        var controller = new MeController(null!, null!, null!, reader, null!) { ControllerContext = new() { HttpContext = http } };
        var reading = controller.Art("Artist", "Song");
        Assert.Equal(TimeSpan.FromSeconds(5), await time.NextTimerAsync());
        var search = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(time.GetUtcNow().AddSeconds(5), search.Deadline);
        var shared = reader.PrepareAsync(Execution(user), "Artist", "Song", null, default);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.IsType<NoContentResult>(await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await shared.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("private, max-age=3600", http.Response.Headers.CacheControl.ToString());
        Assert.True(search.CancellationToken.IsCancellationRequested);
        Assert.Empty(cache.GetKeysByPattern("media:descriptor:*"));
        Assert.Equal(0, handler.Count);
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 8, 0, 0, null), Times.Never);
        blocked.SetResult(Found());
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer")).ReturnsAsync(Found());
        Assert.NotNull(await reader.ReadAsync(Execution(user), "Artist", "Song", null, default));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task ProviderBlockingBeforeReturningItsTaskCannotExceedTheFiveSecondWait()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var gateway = Gateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"))
            .Returns(() => { entered.TrySetResult(); release.Wait(); finished.TrySetResult(); return Task.FromResult(Found()); });
        using var handler = new Covers();
        var reader = new RecapArtworkReader(gateway.Object,
            new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance), new Clients(handler), time: time);
        var reading = Task.Run(() => reader.ReadAsync(Execution(User()), "Artist", "Song", null, default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(5), await time.NextTimerAsync());
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, handler.Count);
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); await reading; }
    }

    [Fact]
    public async Task SlowPreparationCanFillTheCacheWhileAnArtRequestStopsWaitingAfterFiveSeconds()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource<ProtocolExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = Gateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"))
            .Returns((ProtocolExecutionContext context, string _, int _, int _, int _, string? _) => { entered.TrySetResult(context); return release.Task; });
        using var handler = new Covers();
        var writes = new Writes(1);
        var reader = new RecapArtworkReader(gateway.Object,
            new MediaAssetResolver(new TestMemoryApplicationCache(writes), NullLogger<MediaAssetResolver>.Instance), new Clients(handler), time: time);
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        var user = User();
        warmer.Queue(user, Recap() with { TopArtists = [], TopAlbums = [] });
        await warmer.StartAsync(default);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(30), await time.NextTimerAsync());
            var source = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = reader.ReadAsync(Execution(user), "Artist", "Song", null, default);
            Assert.Equal(TimeSpan.FromSeconds(5), await time.NextTimerAsync());
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(source.CancellationToken.IsCancellationRequested);
            time.Advance(TimeSpan.FromSeconds(3));
            release.SetResult(Found());
            await writes.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await reader.ReadAsync(Execution(user), "Artist", "Song", null, default))!.FromCache);
            gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"), Times.Once);
            Assert.Equal(1, handler.Count);
        }
        finally { release.TrySetResult(Found()); await warmer.StopAsync(default); }
    }

    [Fact]
    public async Task ListenerDescriptorsRemainSeparateAndSearchStillUsesTheFirstMatchingCoverInProviderOrder()
    {
        var order = new List<string?>();
        var gateway = Gateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, It.IsAny<string?>()))
            .Returns((ProtocolExecutionContext _, string _, int _, int _, int _, string? provider) =>
            {
                order.Add(provider);
                return Task.FromResult(provider == "deezer" ? new SearchResult() : new SearchResult
                {
                    Songs = [new() { Artist = "Someone else", Title = "Song", CoverArtUrl = "https://covers.example.test/wrong.png" },
                        new() { Artist = "Artist", Title = "Song", CoverArtUrl = "https://covers.example.test/small.png", CoverArtUrlLarge = "https://covers.example.test/large.png" },
                        new() { Artist = "Artist", Title = "Song", CoverArtUrl = "https://covers.example.test/later.png" }]
                });
            });
        var cache = new TestMemoryApplicationCache();
        using var handler = new Covers();
        var reader = new RecapArtworkReader(gateway.Object, new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new Clients(handler));
        var first = Execution(User());
        var second = Execution(User());
        await reader.ReadAsync(first, "Artist", "Song", null, default);
        await reader.ReadAsync(second, "Artist", "Song", null, default);
        Assert.True((await reader.ReadAsync(first, "Artist", "Song", null, default))!.FromCache);
        Assert.Equal(new string?[] { "deezer", null, "deezer", null }, order);
        Assert.Equal(new[] { "/large.png", "/large.png" }, handler.Paths);
        Assert.Equal(2, cache.GetKeysByPattern("media:descriptor:*:recap:recap-song:*:192x0:*").Count());
    }

    [Fact]
    public async Task PreparedRequestAvoidsTheSameSlowSearchAndDownloadAndKeepsTheSameBytes()
    {
        var gateway = Gateway();
        var searches = 0;
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer"))
            .Returns(async () => { Interlocked.Increment(ref searches); await Task.Delay(800); return Found(); });
        using var handler = new Covers(delay: 3000);
        var user = User();
        RecapArtworkReader Reader(TestMemoryApplicationCache cache) => new(gateway.Object,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new Clients(handler));
        var elapsed = Stopwatch.StartNew();
        var before = await Reader(new()).ReadAsync(Execution(user), "Artist", "Song", null, default);
        var cold = elapsed.Elapsed.TotalMilliseconds;
        var writes = new Writes(1);
        var reader = Reader(new(writes));
        using var warmer = new RecapArtworkWarmUp(reader, NullLogger<RecapArtworkWarmUp>.Instance);
        warmer.Queue(user, Recap() with { TopArtists = [], TopAlbums = [] });
        await warmer.StartAsync(default);
        try
        {
            await writes.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var previousSearches = searches;
            var previousDownloads = handler.Count;
            Assert.Equal(2, previousSearches);
            Assert.Equal(2, previousDownloads);
            elapsed.Restart();
            var after = await reader.ReadAsync(Execution(user), "Artist", "Song", null, default);
            var warm = elapsed.Elapsed.TotalMilliseconds;
            Assert.Equal(before!.Bytes, after!.Bytes);
            Assert.False(before.FromCache);
            Assert.True(after.FromCache);
            Assert.Equal(previousSearches, searches);
            Assert.Equal(previousDownloads, handler.Count);
            var measurement = $"Cold request: {cold:F2} ms, 1 provider search + 1 download; prepared request: {warm:F2} ms, 0 searches + 0 downloads; fake search 800 ms, fake download 3000 ms.";
            output.WriteLine(measurement);
            Console.WriteLine(measurement);
        }
        finally { await warmer.StopAsync(default); }
    }

    private static Mock<IProtocolProviderGateway> Gateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Song", 8, 0, 0, "deezer")).ReturnsAsync(Found());
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Album", 0, 8, 0, "deezer"))
            .ReturnsAsync(new SearchResult { Albums = [new() { Artist = "Artist", Title = "Album", CoverArtUrl = "https://covers.example.test/album.png" }] });
        return gateway;
    }

    private static SearchResult Found() => new() { Songs = [new() { Artist = "Artist", Title = "Song", CoverArtUrl = "https://covers.example.test/song.png" }] };
    private static ListeningStats Recap() => new(new(null, DateTimeOffset.UtcNow, "UTC"), 1, 1, 0, null, 0, new(0, 0),
        [new("Song", "Artist", "Album", 1)], [new("Artist", 1)], [new("Album", "Artist", 1)]);
    private static UserRecord User() => new()
    {
        Id = Guid.NewGuid(),
        BackendType = "jellyfin",
        BackendInstanceId = "backend",
        BackendPrincipalId = Guid.NewGuid().ToString(),
        DisplayName = "Listener",
        Enabled = true,
        LastSeenAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    private static ProtocolExecutionContext Execution(UserRecord user) => new(ProtocolKind.Jellyfin, user.BackendInstanceId, user.BackendPrincipalId,
        new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
        "stats-art-test", DateTimeOffset.UtcNow.AddMinutes(1), default);

    private static async Task SeedAsync(Factory factory, UserRecord user, string title, DateTimeOffset at, bool addUser = true, bool cover = false)
    {
        await using var db = factory.CreateDbContext();
        if (addUser) db.Users.Add(user);
        var recording = Guid.NewGuid();
        var identity = Guid.NewGuid();
        if (cover)
        {
            static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = user.Id, Title = title, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording) });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = identity,
                CanonicalRecordingId = recording,
                ProviderId = "deezer",
                ResourceKind = ProviderResourceKind.Track,
                ExternalId = "track",
                ExternalIdHash = Hash("track"),
                Scope = ProviderIdentityScope.Catalog,
                Verification = ProviderIdentityVerification.Verified,
                DecisionVersion = 1,
                Revision = 1
            });
            const string payload = """{"Title":"Song","Artist":"Artist","Artists":["Artist"],"CoverArtUrl":"https://covers.example.test/artist.png"}""";
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user.Id,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendPrincipalId = user.BackendPrincipalId,
                ProviderId = "deezer",
                ResourceKind = "track",
                ExternalIdHash = Hash("track"),
                PayloadJson = payload,
                PayloadSha256 = Hash(payload),
                SnapshotVersion = 1,
                RetrievedAt = at
            });
        }
        db.ListeningEvents.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            OccurrenceKey = Guid.NewGuid().ToString(),
            State = ListeningEventState.Completed,
            ListenedAt = at,
            UpdatedAt = at,
            SourceKind = "protocol",
            TrackReference = title,
            Title = title,
            Artist = "Artist",
            CanonicalRecordingId = cover ? recording : null,
            ProviderTrackIdentityId = cover ? identity : null
        });
        await db.SaveChangesAsync();
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow { get; set; } }
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Covers(int delay = 0) : HttpMessageHandler
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public ConcurrentQueue<string> Paths { get; } = new();
        public byte[] Bytes { get; } = Picture();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) { Headers = { ContentType = new("image/png") } } };
        }
        private static byte[] Picture()
        {
            using var bitmap = new SKBitmap(600, 400);
            var random = new Random(1);
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
    }
    private sealed class Writes(int expected) : IApplicationCacheWriteObserver
    {
        private readonly ConcurrentDictionary<string, byte> keys = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Written(string key)
        {
            if (key.StartsWith("media:descriptor:", StringComparison.Ordinal) && key.Contains(":192x0:", StringComparison.Ordinal))
            {
                keys.TryAdd(key, 0);
                if (keys.Count >= expected) Completed.TrySetResult();
            }
        }
    }
}
