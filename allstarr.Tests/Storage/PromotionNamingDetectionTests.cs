using System.Diagnostics;
using System.Net;
using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class PromotionNamingDetectionTests : IAsyncLifetime
{
    private readonly Guid user = Guid.CreateVersion7();
    private readonly Guid rootId = Guid.CreateVersion7();
    private readonly Backend backend = new();
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private TestBackendLibraryAccess access = null!;
    private PromotionProfiles profiles = null!;
    private PromotionNamingDetectionService detection = null!;
    private MemoryApplicationCache cache = null!;
    private JellyfinProxyService proxy = null!;
    private PromotionRoot[] approved = [];
    private ProtocolExecutionContext Context => new(ProtocolKind.Jellyfin, "backend", "listener",
        new AllstarrPrincipal(user, "jellyfin", "backend", "listener", "Listener", false), "detection",
        DateTimeOffset.UtcNow.AddMinutes(1), default);
    private static HeaderDictionary Headers => new() { ["X-Emby-Token"] = "listener-token" };

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        access = new(factory, "music");
        profiles = new(factory);
        approved = [new(rootId, "Music", "/local/music", "/backend/music", "music")];
        var settings = new Mock<IDurableRuntimeSettings>();
        settings.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var value = JsonSerializer.Serialize(approved);
            return new EffectiveRuntimeSetting(PromotionRoots.Key, RuntimeSettingValueType.String, value, value,
                RuntimeSettingOrigin.Durable, 1, "test", null, null);
        });
        var configuration = new ConfigurationBuilder().Build();
        var roots = new PromotionRoots(settings.Object, factory, configuration);
        cache = new(new SystemPlatformClock());
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(new HttpClient(backend));
        proxy = new(clients.Object, Options.Create(new JellyfinSettings { Url = "http://backend.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), configuration);
        detection = new(factory, profiles, roots, access, proxy, cache);
        await using var db = factory.CreateDbContext();
        db.Users.Add(new()
        {
            Id = user,
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            BackendPrincipalId = "listener",
            DisplayName = "Listener"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DetectsYearFoldersAndReturnsOnlyMatchingRelativeExamples()
    {
        await Seed(8, index => $"Artist/Album (2024)/{index:00} - Title {index}.flac");
        var result = await detection.DetectAsync(Context, rootId, Headers);
        Assert.NotNull(result.Script);
        Assert.Null(result.Reason);
        Assert.Equal(8, result.Matched);
        Assert.Equal(8, result.Sampled);
        Assert.Equal(3, result.Examples.Length);
        Assert.All(result.Examples, example =>
        {
            Assert.StartsWith("Artist/Album (2024)/", example.Path);
            Assert.False(Path.IsPathFullyQualified(example.Path));
            Assert.DoesNotContain("backend", example.Path);
        });
    }

    [Fact]
    public async Task DetectsAlbumArtistAndDiscPrefixWithPaddedTrack()
    {
        await Seed(8, index => $"Collection/Album/{(index % 2) + 1}-{index:00} Title {index}.flac", albumArtist: "Collection",
            disc: index => index % 2 + 1);
        var result = await detection.DetectAsync(Context, rootId, Headers);
        Assert.Equal(8, result.Matched);
        Assert.NotNull(result.Script);
        Assert.Contains("%albumartist%", result.Script);
        Assert.Contains("%discnumber%-", result.Script);
        Assert.Contains("$gt(%totaldiscs%,1)", result.Script);
    }

    [Theory]
    [InlineData("Artist/Album [2024]/Disc 1/{0}. Artist - Title {0}.flac")]
    [InlineData("Artist/Album/CD 1/{0} Title {0}.flac")]
    [InlineData("ARTIST/ALBUM/{0}. TITLE {0}.FLAC")]
    public async Task DetectsSupportedDiscFoldersSeparatorsAndCase(string layout)
    {
        await Seed(8, index => string.Format(System.Globalization.CultureInfo.InvariantCulture, layout, index));
        var result = await detection.DetectAsync(Context, rootId, Headers);
        Assert.NotNull(result.Script);
        Assert.Equal(8, result.Matched);
    }

    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(5, 9, false)]
    [InlineData(6, 10, true)]
    [InlineData(0, 10, false)]
    public async Task RequiresFiveMatchesAndSixtyPercent(int matching, int total, bool confident)
    {
        await Seed(total, index => index <= matching ? $"Artist/Album/{index:00} - Title {index}.flac" : $"Random/{index}/unrelated.flac");
        var result = await detection.DetectAsync(Context, rootId, Headers);
        Assert.Equal(matching, result.Matched);
        Assert.Equal(total, result.Sampled);
        Assert.Equal(confident, result.Script != null);
        Assert.Equal(confident ? null : "no-pattern", result.Reason);
        if (!confident) Assert.Empty(result.Examples);
    }

    [Fact]
    public async Task UsesEffectiveRootAndChecksPermissionsBeforeReadingCache()
    {
        await Seed(8, index => $"Artist/Album/{index:00} - Title {index}.flac");
        await profiles.SaveMineAsync(Context, new() { LibraryRootId = rootId });
        Assert.NotNull((await detection.DetectAsync(Context, null, Headers)).Script);
        var requests = backend.Batches.Count;
        Assert.NotNull((await detection.DetectAsync(Context, null, Headers)).Script);
        Assert.Equal(requests, backend.Batches.Count);
        access.Permissions[user] = new(true, []);
        await Assert.ThrowsAsync<BackendLibraryAccessException>(() => detection.DetectAsync(Context, rootId, Headers));
        access.Permissions[user] = new(true, ["music"]);
        await using var db = factory.CreateDbContext();
        (await db.Users.SingleAsync()).Enabled = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => detection.DetectAsync(Context, rootId, Headers));
    }

    [Fact]
    public async Task LibraryChangesInvalidateCachedDetection()
    {
        await Seed(6, index => $"Artist/Album/{index:00} - Title {index}.flac");
        Assert.Equal(6, (await detection.DetectAsync(Context, rootId, Headers)).Matched);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var track in await db.LibraryTracks.ToArrayAsync()) track.Title = "Changed";
            await db.SaveChangesAsync();
        }
        var result = await detection.DetectAsync(Context, rootId, Headers);
        Assert.Equal(0, result.Matched);
        Assert.Equal("no-pattern", result.Reason);
        Assert.Equal(2, backend.Batches.Count);
    }

    [Fact]
    public async Task CapsDistinctSampleAndBatchesCallerAuthenticatedMetadata()
    {
        await Seed(300, index => $"Artist/Album/{index:00} - Title {index}.flac");
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                WITH RECURSIVE numbers(value) AS (SELECT 301 UNION ALL SELECT value + 1 FROM numbers WHERE value < 20000)
                INSERT INTO library_tracks (Id, OwnerUserId, BackendLibraryId, Protocol, BackendInstanceId, BackendItemId,
                    FilePath, Title, Artist, Album, ProviderIdsJson, IndexedAt, SourceModifiedAt, UpdatedAt, Revision)
                SELECT printf('00000000-0000-0000-0000-%012d', value), template.OwnerUserId, template.BackendLibraryId,
                    template.Protocol, template.BackendInstanceId, 'extra-' || value, '/backend/music/Z/' || value || '.flac',
                    template.Title, template.Artist, template.Album, template.ProviderIdsJson, template.IndexedAt,
                    template.SourceModifiedAt, template.UpdatedAt, template.Revision
                FROM numbers CROSS JOIN (SELECT * FROM library_tracks LIMIT 1) AS template
                """);
            Assert.Equal(20000, await db.LibraryTracks.CountAsync());
        }
        var watch = Stopwatch.StartNew();
        var result = await detection.DetectAsync(Context, rootId, Headers);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Detection took {watch.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.Equal(300, result.Sampled);
        Assert.Equal(300, result.Matched);
        Assert.Equal(3, backend.Batches.Count);
        Assert.All(backend.Batches, batch => Assert.InRange(batch.Length, 1, 100));
        Assert.All(backend.Tokens, token => Assert.Equal("listener-token", token));
    }

    [Fact]
    public async Task RejectsSiblingFolderAndTraversalAndReportsEmptyStates()
    {
        await Seed(6, index => $"Artist/Album/{index:00} - Title {index}.flac");
        await using (var db = factory.CreateDbContext())
        {
            var tracks = await db.LibraryTracks.OrderBy(item => item.BackendItemId).ToArrayAsync();
            tracks[0].FilePath = "/backend/music-other/song.flac";
            tracks[1].FilePath = "/backend/music/../private/song.flac";
            tracks[2].FilePath = "/backend/music/Artist//song.flac";
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, (await detection.DetectAsync(Context, rootId, Headers)).Sampled);
        await using (var db = factory.CreateDbContext())
        {
            db.LibraryTracks.RemoveRange(db.LibraryTracks);
            await db.SaveChangesAsync();
        }
        Assert.Equal("no-songs", (await detection.DetectAsync(Context, rootId, Headers)).Reason);
        approved = [];
        Assert.Equal("no-folder", (await detection.DetectAsync(Context, null, Headers)).Reason);
    }

    private async Task Seed(int count, Func<int, string> relative, string? albumArtist = null, Func<int, int>? disc = null)
    {
        await using var db = factory.CreateDbContext();
        for (var index = 1; index <= count; index++)
        {
            var id = $"song-{index:0000}";
            db.LibraryTracks.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = user,
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendLibraryId = "music",
                BackendItemId = id,
                FilePath = "/backend/music/" + relative(index),
                Title = $"Title {index}",
                Artist = "Artist",
                Album = "Album",
                AlbumArtist = albumArtist
            });
            backend.Items[id] = new { Id = id, IndexNumber = index, ParentIndexNumber = disc?.Invoke(index) ?? 1, ProductionYear = 2024 };
        }
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        proxy.Dispose();
        cache.Dispose();
        await database.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class Backend : HttpMessageHandler
    {
        public Dictionary<string, object> Items { get; } = [];
        public List<string[]> Batches { get; } = [];
        public List<string?> Tokens { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ids = QueryHelpers.ParseQuery(request.RequestUri!.Query)["Ids"].ToString().Split(',');
            Batches.Add(ids);
            var headers = new HeaderDictionary();
            foreach (var header in request.Headers) headers[header.Key] = header.Value.ToArray();
            Tokens.Add(AuthHeaderHelper.ExtractToken(headers));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                JsonSerializer.Serialize(new { Items = ids.Where(Items.ContainsKey).Select(id => Items[id]).ToArray() }))
            });
        }
    }
}
