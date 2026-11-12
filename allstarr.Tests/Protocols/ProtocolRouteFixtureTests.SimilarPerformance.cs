using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task Jellyfin_SimilarBatchesRankedAlbumsAndPreservesNativeFieldsAndProviderOrder()
    {
        var reads = new SimilarLibraryReads();
        var gateway = SimilarPerformanceGateway();
        using var factory = SimilarPerformanceFactory(reads, gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/Items/native-seed/Similar?Limit=3&Fields=Genres,Overview,PrimaryImageAspectRatio,ProductionYear,ChildCount");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "quartz-best", "velvet-best", "ext-deezer-album-cedar-new" }, items.Select(item => item.GetProperty("Id").GetString()));
        Assert.Equal("deezer", body.RootElement.GetProperty("AllstarrBasedOn").GetString());
        Assert.Equal("native-overview", items[0].GetProperty("Overview").GetString());
        Assert.Equal("native-art", items[0].GetProperty("ImageTags").GetProperty("Primary").GetString());
        Assert.Equal("backdrop-art", items[0].GetProperty("BackdropImageTags")[0].GetString());
        Assert.Equal(1.25, items[0].GetProperty("PrimaryImageAspectRatio").GetDouble());
        Assert.Equal("Rock", items[0].GetProperty("Genres")[0].GetString());
        Assert.Equal(2010, items[0].GetProperty("ProductionYear").GetInt32());
        Assert.Equal(9, items[0].GetProperty("ChildCount").GetInt32());
        Assert.Equal(19, items[0].GetProperty("UserData").GetProperty("PlayCount").GetInt32());
        Assert.Equal("library", items[0].GetProperty("AllstarrSource").GetString());
        Assert.Equal("jellyfin", items[0].GetProperty("AllstarrProvider").GetProperty("id").GetString());
        Assert.Equal("Newest full album [A]", items[2].GetProperty("Name").GetString());
        var library = reads.Requests.Where(uri => uri.AbsolutePath is "/Users/listener/Items" or "/Artists/AlbumArtists").ToArray();
        Assert.InRange(library.Length, 1, 2);
        var bulk = Assert.Single(library, uri => QueryHelpers.ParseQuery(uri.Query).ContainsKey("includeItemTypes"));
        Assert.Equal("PlayCount,CommunityRating,ProductionYear", QueryHelpers.ParseQuery(bulk.Query)["sortBy"].ToString());
        Assert.Equal("Descending", QueryHelpers.ParseQuery(bulk.Query)["sortOrder"].ToString());
        Assert.DoesNotContain(library, uri => QueryHelpers.ParseQuery(uri.Query).ContainsKey("albumArtistIds"));
        Assert.DoesNotContain(library, uri => QueryHelpers.ParseQuery(uri.Query).ContainsKey("Ids"));
        gateway.Verify(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "quartz", It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Jellyfin_SimilarOverlapsSourcesAndLibraryAndReusesOnlyMatchingFreshResults()
    {
        var reads = new SimilarLibraryReads { Delay = 25 };
        var gateway = SimilarPerformanceGateway();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceCalls = 0;
        reads.BulkStarted = started;
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "performance-seed", It.IsAny<int>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref sourceCalls) == 1)
                {
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    Assert.False(reads.BulkFinished);
                }
                await Task.Delay(30);
                return SimilarPerformanceArtists();
            });
        var clock = new SimilarClock();
        using var factory = SimilarPerformanceFactory(reads, gateway, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        const string path = "/Items/ext-deezer-album-performance-seed/Similar?Limit=3";
        using var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();
        var expected = await first.Content.ReadAsStringAsync();
        Assert.Contains("AllstarrBasedOn", expected);
        var calls = reads.Requests.Count;
        using var cached = await client.GetAsync(path);
        cached.EnsureSuccessStatusCode();
        Assert.Equal(expected, await cached.Content.ReadAsStringAsync());
        Assert.Equal(calls, reads.Requests.Count);
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "performance-seed", It.IsAny<int>()), Times.Once);

        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "performance-seed", It.IsAny<int>()))
            .ReturnsAsync(SimilarPerformanceArtists());
        using var fields = await client.GetAsync(path + "&Fields=Genres");
        fields.EnsureSuccessStatusCode();
        Assert.True(reads.Requests.Count > calls);
        calls = reads.Requests.Count;
        reads.VisibleLibraries = ["other-music"];
        using var visibility = await client.GetAsync(path);
        visibility.EnsureSuccessStatusCode();
        Assert.True(reads.Requests.Count > calls);
        calls = reads.Requests.Count;
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.LibraryTracks.Add(new() { Id = Guid.NewGuid(), OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", BackendItemId = "similar-added" });
            await db.SaveChangesAsync();
        }
        using var libraryChanged = await client.GetAsync(path);
        libraryChanged.EnsureSuccessStatusCode();
        Assert.True(reads.Requests.Count > calls);
        calls = reads.Requests.Count;
        var other = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "other")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = other,
                ProviderId = "allstarr",
                ResourceKind = "library-artist-fix",
                ExternalIdHash = CanonicalCatalogKeys.Hash("similar-household-fix"),
                PayloadSha256 = CanonicalCatalogKeys.Hash("[]"),
                SnapshotVersion = 1,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "other",
                PayloadJson = "[]",
                RetrievedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using var householdChanged = await client.GetAsync(path);
        householdChanged.EnsureSuccessStatusCode();
        Assert.True(reads.Requests.Count > calls);
        calls = reads.Requests.Count;
        clock.Now = clock.Now.AddMinutes(3);
        using var expired = await client.GetAsync(path);
        expired.EnsureSuccessStatusCode();
        Assert.True(reads.Requests.Count > calls);
    }

    [Fact]
    public async Task Jellyfin_SimilarReportsSyntheticBeforeAndAfterRequestTiming()
    {
        RequestTiming.Result before;
        JsonElement[] previous;
        using (var timing = RequestTiming.Begin())
        using (var handler = new RequestTimingHandler(true) { InnerHandler = new SimilarLibraryReads { Delay = 15 } })
        using (var backend = new HttpClient(handler) { BaseAddress = new Uri("http://backend") })
        {
            previous = await PreviousSimilarNativePicksAsync(backend);
            before = timing.Snapshot("similar-synthetic-before");
        }
        var reads = new SimilarLibraryReads { Delay = 15 };
        using var factory = SimilarPerformanceFactory(reads, SimilarPerformanceGateway());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var cold = await client.GetAsync("/Items/native-seed/Similar?Limit=3");
        cold.EnsureSuccessStatusCode();
        using var warm = await client.GetAsync("/Items/native-seed/Similar?Limit=3");
        warm.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await cold.Content.ReadAsStringAsync());
        var native = body.RootElement.GetProperty("Items").EnumerateArray()
            .Where(item => item.GetProperty("AllstarrSource").GetString() == "library").Select(item =>
            {
                var row = JsonNode.Parse(item.GetRawText())!.AsObject();
                row.Remove("AllstarrSource");
                row.Remove("AllstarrProvider");
                return JsonSerializer.SerializeToElement(row);
            }).ToArray();
        Assert.Equal(previous.Select(item => item.GetRawText()), native.Select(item => item.GetRawText()));
        _lyricsSearchOutput.WriteLine("Synthetic old lookup loop: same seed and related artists, 15 ms per backend call, concurrency 4; " + before.Header);
        _lyricsSearchOutput.WriteLine("Synthetic optimized cold: " + Assert.Single(cold.Headers.GetValues("Server-Timing")));
        _lyricsSearchOutput.WriteLine("Synthetic optimized warm: " + Assert.Single(warm.Headers.GetValues("Server-Timing")));
        foreach (var concurrent in await ReadConcurrentTimingAsync(client, "/Items/native-seed/Similar?Limit=3"))
            Assert.Equal(await cold.Content.ReadAsByteArrayAsync(), concurrent);
        Assert.Equal(7, before.JellyfinCalls);
        Assert.InRange(reads.Requests.Count(uri => uri.AbsolutePath is "/Users/listener/Items" or "/Artists/AlbumArtists"), 1, 2);
        Assert.Equal(await cold.Content.ReadAsStringAsync(), await warm.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jellyfin_SimilarFallsBackWithoutCachingUnavailableBulkReads(bool albumsUnavailable)
    {
        var reads = new SimilarLibraryReads { AlbumsUnavailable = albumsUnavailable, ArtistsUnavailable = !albumsUnavailable };
        var gateway = SimilarPerformanceGateway();
        using var factory = SimilarPerformanceFactory(reads, gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        const string path = "/Items/ext-deezer-album-performance-seed/Similar?Limit=3";
        using var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();
        var calls = reads.Requests.Count;
        using var again = await client.GetAsync(path);
        again.EnsureSuccessStatusCode();
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.True(reads.Requests.Count > calls);
        using var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        var ids = body.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetString()).ToArray();
        Assert.Equal(new[] { "quartz-best", "velvet-best", "ext-deezer-album-cedar-new" }, ids);
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "performance-seed", It.IsAny<int>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jellyfin_SimilarKeepsTheFirstExactArtistWithinTheOldSearchLimit(bool truncated)
    {
        var reads = new SimilarLibraryReads { Ambiguous = true, Truncated = truncated };
        JsonElement[] previous;
        using (var backend = new HttpClient(reads, disposeHandler: false) { BaseAddress = new Uri("http://backend") })
            previous = await PreviousSimilarNativePicksAsync(backend);
        reads.Requests.Clear();
        using var factory = SimilarPerformanceFactory(reads, SimilarPerformanceGateway());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/Items/native-seed/Similar?Limit=3");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var native = body.RootElement.GetProperty("Items").EnumerateArray()
            .Where(item => item.GetProperty("AllstarrSource").GetString() == "library").ToArray();
        Assert.Equal(previous.Select(item => item.GetProperty("Id").GetString()), native.Select(item => item.GetProperty("Id").GetString()));
        Assert.DoesNotContain(native, item => item.GetProperty("Id").GetString() == "quartz-namesake");
        var lookup = Assert.Single(reads.Requests, uri => QueryHelpers.ParseQuery(uri.Query).TryGetValue("searchTerm", out var term) && term.ToString() == "Quartz");
        Assert.Equal("5", QueryHelpers.ParseQuery(lookup.Query)["limit"].ToString());
        Assert.Equal(!truncated, native.Any(item => item.GetProperty("Id").GetString() == "quartz-best"));
    }

    private static async Task<JsonElement[]> PreviousSimilarNativePicksAsync(HttpClient backend)
    {
        using (RequestTiming.Current?.Stage("library"))
        using (var seed = await backend.GetAsync("/Users/listener/Items/native-seed")) seed.EnsureSuccessStatusCode();
        using var slots = new SemaphoreSlim(4);
        var picks = await Task.WhenAll(SimilarPerformanceArtists().Select(async artist =>
        {
            await slots.WaitAsync();
            try
            {
                using var artists = await backend.GetAsync("/Artists/AlbumArtists?userId=listener&searchTerm=" + Uri.EscapeDataString(artist.Name) + "&limit=5&enableImages=false");
                using var found = JsonDocument.Parse(await artists.Content.ReadAsStringAsync());
                var match = found.RootElement.GetProperty("Items").EnumerateArray().FirstOrDefault(item =>
                    FuzzyMatcher.NormalizeForMatching(item.GetProperty("Name").GetString()!) == FuzzyMatcher.NormalizeForMatching(artist.Name));
                if (match.ValueKind != JsonValueKind.Object) return null;
                using var albums = await backend.GetAsync("/Users/listener/Items?includeItemTypes=MusicAlbum&recursive=true&albumArtistIds=" +
                    match.GetProperty("Id").GetString() + "&sortBy=PlayCount,CommunityRating,ProductionYear&sortOrder=Descending&limit=1&fields=ChildCount,ProductionYear,AlbumArtist&enableImages=true");
                using var rows = JsonDocument.Parse(await albums.Content.ReadAsStringAsync());
                return rows.RootElement.GetProperty("Items").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } album
                    ? album.GetProperty("Id").GetString() : null;
            }
            finally { slots.Release(); }
        }));
        var ids = picks.OfType<string>().ToArray();
        using var native = await backend.GetAsync("/Users/listener/Items?Ids=" + string.Join(',', ids) + "&Fields=PrimaryImageAspectRatio,ProductionYear,ChildCount");
        using var document = JsonDocument.Parse(await native.Content.ReadAsStringAsync());
        var rowsById = document.RootElement.GetProperty("Items").EnumerateArray().ToDictionary(item => item.GetProperty("Id").GetString()!);
        return ids.Select(id => rowsById[id].Clone()).ToArray();
    }

    private static List<Artist> SimilarPerformanceArtists() =>
    [
        new() { Name = "Quartz", ExternalProvider = "deezer", ExternalId = "quartz" },
        new() { Name = "Velvet", ExternalProvider = "deezer", ExternalId = "velvet" },
        new() { Name = "Cedar", ExternalProvider = "deezer", ExternalId = "cedar" }
    ];

    private static Mock<IProtocolProviderGateway> SimilarPerformanceGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "performance-seed"))
            .ReturnsAsync(new Album { Title = "Seed album", Artist = "Seed", ArtistId = "ext-deezer-artist-performance-seed" });
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Seed album", 0, 5, 0, "deezer"))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult { Albums = [new() { Title = "Seed album", Artist = "Seed", ArtistId = "ext-deezer-artist-performance-seed", ExternalProvider = "deezer" }] });
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "performance-seed", It.IsAny<int>()))
            .ReturnsAsync(SimilarPerformanceArtists());
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "quartz", It.IsAny<int>()))
            .ReturnsAsync([new() { Id = "ext-deezer-album-quartz-remote", Title = "Quartz remote", Artist = "Quartz" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "cedar", It.IsAny<int>()))
            .ReturnsAsync([
                new() { Id = "ext-deezer-album-cedar-single", Title = "New single", Artist = "Cedar", ReleaseType = "single", ReleaseDate = "2026-11-01" },
                new() { Id = "ext-deezer-album-cedar-old", Title = "Older full album", Artist = "Cedar", ReleaseDate = "2019-01-01" },
                new() { Id = "ext-deezer-album-cedar-new", Title = "Newest full album", Artist = "Cedar", ReleaseDate = "2020-01-01" }
            ]);
        return gateway;
    }

    private static ProtocolFactory SimilarPerformanceFactory(SimilarLibraryReads reads, Mock<IProtocolProviderGateway> gateway,
        Action<IServiceCollection>? configure = null) => ClientDetailsFactory(_ => Json(404, "{}"), services =>
    {
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway.Object);
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new RequestTimingHandler(true) { InnerHandler = reads }));
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BackendLibraryAccess(true, reads.VisibleLibraries));
        access.Setup(service => service.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendLibraryAccessContext.Unavailable);
        services.RemoveAll<IBackendLibraryAccessResolver>();
        services.AddSingleton(access.Object);
        configure?.Invoke(services);
    });

    private sealed class SimilarClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SimilarLibraryReads : HttpMessageHandler
    {
        public readonly ConcurrentBag<Uri> Requests = [];
        public int Delay;
        public bool Ambiguous;
        public bool Truncated;
        public bool AlbumsUnavailable;
        public bool ArtistsUnavailable;
        public string[] VisibleLibraries = ["music"];
        public TaskCompletionSource? BulkStarted;
        public bool BulkFinished;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            var query = QueryHelpers.ParseQuery(uri.Query);
            var bulk = uri.AbsolutePath == "/Users/listener/Items" && query.ContainsKey("includeItemTypes") && !query.ContainsKey("albumArtistIds");
            if (bulk) BulkStarted?.TrySetResult();
            if (Delay > 0) await Task.Delay(Delay, cancellationToken);
            if (bulk) BulkFinished = true;
            if (bulk && AlbumsUnavailable || uri.AbsolutePath == "/Artists/AlbumArtists" && !query.ContainsKey("searchTerm") && ArtistsUnavailable)
                return Json(503, "{}");
            const string rows = """{"Items":[{"Id":"velvet-best","Type":"MusicAlbum","Name":"Velvet favorite","AlbumArtist":"Velvet","AlbumArtists":[{"Id":"velvet-id","Name":"Velvet"}],"ProductionYear":1999},{"Id":"quartz-best","Type":"MusicAlbum","Name":"Quartz favorite","AlbumArtist":"Quartz","AlbumArtists":[{"Id":"quartz-id","Name":"Quartz"}],"ProductionYear":2010,"ChildCount":9,"Overview":"native-overview","Genres":["Rock"],"PrimaryImageAspectRatio":1.25,"ImageTags":{"Primary":"native-art"},"BackdropImageTags":["backdrop-art"],"UserData":{"PlayCount":19}},{"Id":"quartz-new","Type":"MusicAlbum","Name":"Quartz newest","AlbumArtist":"Quartz","AlbumArtists":[{"Id":"quartz-id","Name":"Quartz"}],"ProductionYear":2025},{"Id":"native-seed","Type":"MusicAlbum","Name":"Seed album","AlbumArtist":"Seed","AlbumArtists":[{"Id":"seed-id","Name":"Seed"}]}]}""";
            using var document = JsonDocument.Parse(rows);
            var albums = document.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.Clone()).ToList();
            if (Ambiguous)
                albums.Insert(0, JsonSerializer.SerializeToElement(new { Id = "quartz-namesake", Type = "MusicAlbum", Name = "Wrong namesake", AlbumArtist = "Quartz", AlbumArtists = new[] { new { Id = "quartz-other", Name = "Quartz" } } }));
            if (bulk) return Json(200, JsonSerializer.Serialize(new { Items = albums }));
            if (uri.AbsolutePath == "/Artists/AlbumArtists")
            {
                var artists = new List<object> { new { Id = "quartz-id", Name = "Quartz" }, new { Id = "velvet-id", Name = "Velvet" }, new { Id = "seed-id", Name = "Seed" } };
                if (Ambiguous) artists.Insert(0, new { Id = "quartz-other", Name = "Quartz" });
                var term = query.GetValueOrDefault("searchTerm").ToString();
                if (term == "Quartz" && Ambiguous)
                    return Json(200, JsonSerializer.Serialize(new
                    {
                        Items = Enumerable.Range(0, Truncated ? 5 : 4)
                        .Select(number => (object)new { Id = "tribute-" + number, Name = "Quartz Tribute " + number })
                        .Concat(Truncated ? [] : new object[] { new { Id = "quartz-id", Name = "Quartz" } })
                    }));
                return Json(200, JsonSerializer.Serialize(new
                {
                    Items = term.Length == 0 ? artists : artists.Where(item =>
                    JsonSerializer.SerializeToElement(item).GetProperty("Name").GetString() == term).ToList()
                }));
            }
            if (uri.AbsolutePath == "/Users/listener/Items" && query.ContainsKey("albumArtistIds"))
                return Json(200, JsonSerializer.Serialize(new
                {
                    Items = albums.Where(item => item.GetProperty("AlbumArtists").EnumerateArray()
                    .Any(credit => credit.GetProperty("Id").GetString() == query["albumArtistIds"].ToString())).Take(1)
                }));
            if ((uri.AbsolutePath is "/Users/listener/Items" or "/Items") && query.ContainsKey("Ids"))
                return Json(200, JsonSerializer.Serialize(new { Items = albums.Where(item => query["Ids"].ToString().Split(',').Contains(item.GetProperty("Id").GetString())) }));
            if (uri.AbsolutePath == "/Users/listener/Items/native-seed")
                return Json(200, """{"Id":"native-seed","Type":"MusicAlbum","Name":"Seed album","AlbumArtist":"Seed"}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }
    }
}
