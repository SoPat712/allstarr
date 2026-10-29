using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using allstarr.Core.Enrichment;
using allstarr.Core.Operations;
using allstarr.Models.Settings;
using allstarr.Services.MusicBrainz;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class SharedTagSourcesTests
{
    private const string RecordingId = "31e68c1d-31f9-432c-a3a4-13aef4a53833";
    private const string ArtistId = "11e68c1d-31f9-432c-a3a4-13aef4a53833";
    private const string ReleaseGroupId = "21e68c1d-31f9-432c-a3a4-13aef4a53833";

    [Fact]
    public async Task CollectRecording_UsesExactEvidenceAndEmbeddedReleaseGroupTags()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        musicBrainz.Setup(item => item.LookupByMbidAsync(RecordingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzRecording
            {
                Id = RecordingId,
                Genres = [new() { Name = "rock", Count = 4 }],
                Tags = [new() { Name = "live", Count = 2 }],
                ArtistCredit = [new() { Artist = new() { Id = ArtistId } }],
                Releases = [new() { ReleaseGroup = new MusicBrainzReleaseGroup
                {
                    Id = ReleaseGroupId,
                    Genres = [new() { Name = "indie", Count = 3 }],
                    Tags = [new() { Name = "concert", Count = 1 }]
                }}]
            });
        var source = Create(musicBrainz.Object, new ScrobblingSettings(),
            new ThrowingHttpClientFactory(), new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

        var result = await source.CollectRecordingAsync(RecordingId, null, "Artist", "Song", CancellationToken.None);

        Assert.Equal(ArtistId, result.MusicBrainzArtistId);
        Assert.Collection(result.Batches,
            batch =>
            {
                Assert.Equal("musicbrainz-recording", batch.Source);
                Assert.Contains(batch.Tags, tag => tag.RawLabel == "rock" && tag.IsGenre && tag.Weight == 4);
                Assert.Contains(batch.Tags, tag => tag.RawLabel == "live" && !tag.IsGenre && tag.Weight == 2);
            },
            batch =>
            {
                Assert.Equal("musicbrainz-release-group", batch.Source);
                Assert.Contains(batch.Tags, tag => tag.RawLabel == "indie" && tag.IsGenre && tag.Weight == 3);
                Assert.Contains(batch.Tags, tag => tag.RawLabel == "concert" && !tag.IsGenre && tag.Weight == 1);
            });
        musicBrainz.Verify(item => item.LookupByIsrcAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CollectRecording_KeylessHouseholdSkipsLastFm()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        musicBrainz.Setup(item => item.LookupByIsrcAsync("USABC1234567", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MusicBrainzRecording?)null);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var source = Create(musicBrainz.Object, new ScrobblingSettings(), clients.Object,
            DateTimeOffset.UnixEpoch);

        var result = await source.CollectRecordingAsync(null, "USABC1234567", "Artist", "Song", CancellationToken.None);

        Assert.All(result.Batches, batch => Assert.Empty(batch.Tags));
        clients.Verify(item => item.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CollectRecording_FallsBackToIsrcWhenMbidIsMissingFromTheCatalog()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        musicBrainz.Setup(item => item.LookupByMbidAsync(RecordingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MusicBrainzRecording?)null);
        musicBrainz.Setup(item => item.LookupByIsrcAsync("USABC1234567", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzRecording { Id = RecordingId, Genres = [new() { Name = "rock", Count = 4 }] });
        var source = Create(musicBrainz.Object, new ScrobblingSettings(), new ThrowingHttpClientFactory(), DateTimeOffset.UnixEpoch);

        var result = await source.CollectRecordingAsync(RecordingId, "USABC1234567", "Artist", "Song", default);

        Assert.Contains(result.Batches.SelectMany(batch => batch.Tags), tag => tag.RawLabel == "rock" && tag.Weight == 4);
    }

    [Fact]
    public async Task CollectArtist_ParsesLastFmSingletonAndStringCount()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        musicBrainz.Setup(item => item.LookupArtistByMbidAsync(ArtistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MusicBrainzArtist
            {
                Id = ArtistId,
                Genres = [new() { Name = "pop", Count = 8 }]
            });
        var handler = new QueueHandler("""{"toptags":{"tag":{"name":"electro","count":"7"}}}""");
        var source = Create(musicBrainz.Object,
            new ScrobblingSettings { LastFm = new() { ApiKey = "household-key" } },
            new ClientFactory(handler), DateTimeOffset.UnixEpoch);

        var batches = await source.CollectArtistAsync(ArtistId, "Artist", CancellationToken.None);

        Assert.Collection(batches,
            batch =>
            {
                Assert.Equal("musicbrainz-artist", batch.Source);
                Assert.Contains(batch.Tags, tag => tag.RawLabel == "pop" && tag.IsGenre && tag.Weight == 8);
            },
            batch =>
            {
                Assert.Equal("lastfm-artist", batch.Source);
                var tag = Assert.Single(batch.Tags);
                Assert.Equal("electro", tag.RawLabel);
                Assert.Equal(7d, tag.Weight);
                Assert.False(tag.IsGenre);
            });
        Assert.Contains("method=artist.getTopTags", handler.RequestUris.Single().Query, StringComparison.Ordinal);
        Assert.DoesNotContain("session", handler.RequestUris.Single().Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", handler.RequestUris.Single().Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectArtist_EmptyOrMissingLastFmTagsReplaceOldEvidence()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        var settings = new ScrobblingSettings { LastFm = new() { ApiKey = "household-key" } };
        var firstHandler = new QueueHandler("""{"toptags":{"tag":""}}""");
        var secondHandler = new QueueHandler("""{"toptags":{}}""");
        var first = Create(musicBrainz.Object, settings, new ClientFactory(firstHandler), DateTimeOffset.UnixEpoch);
        var second = Create(musicBrainz.Object, settings, new ClientFactory(secondHandler), DateTimeOffset.UnixEpoch);

        Assert.Empty(Assert.Single(await first.CollectArtistAsync(null, "Artist", CancellationToken.None)).Tags);
        Assert.Empty(Assert.Single(await second.CollectArtistAsync(null, "Artist", CancellationToken.None)).Tags);
    }

    [Fact]
    public async Task CollectRecording_ParsesLastFmTagArray()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        var handler = new QueueHandler("""{"toptags":{"tag":[{"name":"rock","count":3},{"name":"indie"}]}}""");
        var source = Create(musicBrainz.Object,
            new ScrobblingSettings { LastFm = new() { ApiKey = "household-key" } },
            new ClientFactory(handler), DateTimeOffset.UnixEpoch);

        var result = await source.CollectRecordingAsync(null, null, "Artist", "Song", CancellationToken.None);

        var batch = Assert.Single(result.Batches);
        Assert.Equal("lastfm-track", batch.Source);
        Assert.Contains("method=track.getTopTags", handler.RequestUris.Single().Query, StringComparison.Ordinal);
        Assert.Contains("track=Song", handler.RequestUris.Single().Query, StringComparison.Ordinal);
        var tags = batch.Tags;
        Assert.Equal(2, tags.Count);
        Assert.Equal(3d, tags[0].Weight);
        Assert.Null(tags[1].Weight);
    }

    [Fact]
    public async Task CollectArtist_RejectedLastFmKeyDoesNotClearCachedEvidence()
    {
        var handler = new QueueHandler("""{"error":10,"message":"Invalid API key"}""");
        var source = Create(Mock.Of<IMusicBrainzCatalogClient>(),
            new ScrobblingSettings { LastFm = new() { ApiKey = "fixture-key" } }, new ClientFactory(handler), DateTimeOffset.UnixEpoch);

        Assert.Empty(await source.CollectArtistAsync(null, "Artist", default));
    }

    [Fact]
    public async Task CollectArtist_LastFmRateLimitErrorIsRetryable()
    {
        var musicBrainz = new Mock<IMusicBrainzCatalogClient>(MockBehavior.Strict);
        var handler = new QueueHandler("""{"error":29,"message":"Rate limit exceeded"}""");
        var source = Create(musicBrainz.Object,
            new ScrobblingSettings { LastFm = new() { ApiKey = "household-key" } },
            new ClientFactory(handler), DateTimeOffset.UnixEpoch);

        var error = await Assert.ThrowsAsync<MusicBrainzLookupException>(() =>
            source.CollectArtistAsync(null, "Artist", CancellationToken.None));

        Assert.True(error.Retryable);
        Assert.DoesNotContain("Rate limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectArtist_SpacesRequestsForTheSource()
    {
        var handler = new QueueHandler("""{"toptags":{"tag":[]}}""", """{"toptags":{"tag":[]}}""");
        var source = Create(Mock.Of<IMusicBrainzCatalogClient>(),
            new ScrobblingSettings { LastFm = new() { ApiKey = "fixture-key" } }, new ClientFactory(handler), DateTimeOffset.UnixEpoch);
        var started = Stopwatch.StartNew();
        await source.CollectArtistAsync(null, "First", default);
        await source.CollectArtistAsync(null, "Second", default);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(900));
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public void MusicBrainz_ParsesGenresTagsAndStringCountsFromPublicResponseShape()
    {
        var recording = JsonSerializer.Deserialize<MusicBrainzRecording>("""
            {"id":"31e68c1d-31f9-432c-a3a4-13aef4a53833",
             "genres":[{"name":"Rock","count":"4"}], "tags":[{"name":"seen live","count":-1}],
             "artist-credit":[{"artist":{"id":"11e68c1d-31f9-432c-a3a4-13aef4a53833",
               "genres":[{"name":"Blues","count":2}]}}],
             "releases":[{"release-group":{"genres":[{"name":"Soul","count":"3"}]}}]}
            """)!;
        Assert.Equal(4, Assert.Single(recording.Genres!).Count);
        Assert.Equal(-1, Assert.Single(recording.Tags!).Count);
        Assert.Equal(2, Assert.Single(Assert.Single(recording.ArtistCredit!).Artist!.Genres!).Count);
        Assert.Equal(3, Assert.Single(Assert.Single(recording.Releases!).ReleaseGroup!.Genres!).Count);
    }

    private static SharedTagSources Create(IMusicBrainzCatalogClient musicBrainz,
        ScrobblingSettings settings, IHttpClientFactory clients, DateTimeOffset now) =>
        new(musicBrainz, clients, new StaticOptionsMonitor<ScrobblingSettings>(settings),
            new FixedClock(now));

    private sealed class FixedClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string> listener) => Noop.Instance;
        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Last.fm must not be called.");
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient client = new(handler);
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class QueueHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(bodies);
        public List<Uri> RequestUris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(queue.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }
}
