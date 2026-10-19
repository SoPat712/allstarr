using System.Net;
using System.Net.Http.Headers;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.MusicBrainz;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class OdesliServiceTests
{
    private const string Source = "https://tidal.com/browse/track/42";
    private const string WebLinks = """
        <html><body><!-- <a href="https://deezer.com/track/999">ignore</a> -->
        <script>const example = '<a href="https://deezer.com/track/888">';</script>
        <A class='platform' HREF='https://open.spotify.com/track/abc123?si=x&amp;foo=y'>Listen</A>
        <a href=https://music.apple.com/us/album/sample/7?i=81>Apple</a>
        <a href="https://deezer.com.example.test/track/999">Wrong host</a>
        <a href="https://name:secret@deezer.com/track/999">Credentials</a>
        <a href="https://open.spotify.com/search/example">Search</a></body></html>
        """;

    [Fact]
    public async Task MissingKey_UsesWebThenJsonFallbackAndCachesSharedTranslations()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "song.link" => Text(WebLinks),
            "api.unitune.art" => Text("""{"linksByPlatform":{"tidal":{"url":"https://tidal.com/browse/track/42"},"deezer":{"url":"https://deezer.com/track/91"}}}"""),
            _ => new(HttpStatusCode.NotFound)
        }));
        var service = Create(handler);
        var links = await service.GetTrackLinksAsync(Source, default);
        Assert.Equal("abc123", links["spotify"]);
        Assert.Equal("81", links["apple-musickit"]);
        Assert.Equal("91", links["deezer"]);
        Assert.Equal("abc123", await service.ConvertTidalToSpotifyIdAsync("42"));
        Assert.Equal("abc123", await service.TranslateCachedTrackUrlAsync(Source, "spotify"));
        Assert.Equal(new[] { "song.link", "api.unitune.art" }, handler.Hosts);
        Assert.Contains("text/html", handler.Accepts[0]);
    }

    [Fact]
    public async Task EnoughPlatformLinks_StopTheChainBeforeLaterServices()
    {
        var handler = new Handler((_, _) => Task.FromResult(Text("""
            <a href='https://open.spotify.com/track/abc123'>Spotify</a>
            <a href='https://deezer.com/track/71'>Deezer</a>
            <a href='https://open.qobuz.com/track/72'>Qobuz</a>
            """)));
        var links = await Create(handler).GetTrackLinksAsync(Source, default);
        Assert.Equal(4, links.Count);
        Assert.Equal("song.link", Assert.Single(handler.Hosts));
    }

    [Fact]
    public async Task ConfiguredKey_IsTriedFirstAndIsNeverSentToFallbackHosts()
    {
        var handler = new Handler((request, _) =>
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (request.RequestUri.Host == "api.song.link")
            {
                Assert.Equal("fixture +/& key", query["key"]);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            Assert.False(query.ContainsKey("key"));
            Assert.DoesNotContain("fixture", request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
            return Task.FromResult(request.RequestUri.Host == "song.link" ? Text(WebLinks) : new(HttpStatusCode.NotFound));
        });
        Assert.Equal("abc123", (await Create(handler, apiKey: "fixture +/& key").GetTrackLinksAsync(Source, default))["spotify"]);
        Assert.Equal(new[] { "api.song.link", "song.link", "api.unitune.art", "squigly.link" }, handler.Hosts);
    }

    [Fact]
    public async Task SuccessfulOfficialApi_ReturnsWithoutPublicFallback()
    {
        var handler = new Handler((_, _) => Task.FromResult(Text("""
            {"linksByPlatform":{"tidal":{"url":"https://tidal.com/browse/track/42"},
            "spotify":{"url":"https://open.spotify.com/track/abc123"}}}
            """)));
        Assert.Equal("abc123", await Create(handler, apiKey: "fixture-key").ConvertTidalToSpotifyIdAsync("42"));
        Assert.Equal("api.song.link", Assert.Single(handler.Hosts));
    }

    [Theory]
    [InlineData("https://example.test/page")]
    [InlineData("https://song.link.example.test/page")]
    [InlineData("http://song.link/page")]
    [InlineData("https://name:secret@song.link/page")]
    [InlineData("https://song.link:444/page")]
    public async Task WebRedirect_RejectsUnexpectedDestinationsBeforeSending(string destination)
    {
        var handler = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(destination);
            return Task.FromResult(request.RequestUri!.Host == "song.link" ? response : new(HttpStatusCode.NotFound));
        });
        await Assert.ThrowsAsync<OdesliLookupException>(() => Create(handler).GetTrackLinksAsync(Source, default));
        Assert.Equal(new[] { "song.link", "api.unitune.art", "squigly.link" }, handler.Hosts);
    }

    [Fact]
    public async Task WebRedirect_FollowsRelativeAndApprovedHosts()
    {
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "song.link")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://album.link/example");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(request.RequestUri.Host == "album.link" ? Text(WebLinks) : new(HttpStatusCode.NotFound));
        });
        Assert.Equal("abc123", (await Create(handler).GetTrackLinksAsync(Source, default))["spotify"]);
        Assert.Equal("album.link", handler.Hosts[1]);
    }

    [Fact]
    public async Task LaterTimeout_KeepsPartialLinks()
    {
        var handler = new Handler((request, _) => request.RequestUri!.Host == "song.link"
            ? Task.FromResult(Text(WebLinks)) : throw new TaskCanceledException("Request expired"));
        Assert.Equal("abc123", (await Create(handler).GetTrackLinksAsync(Source, default))["spotify"]);
    }

    [Fact]
    public async Task RequestDeadline_StopsAfterSixSecondsAndKeepsEarlierLinks()
    {
        var clock = new ManualTimeProvider();
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "song.link") return Text(WebLinks);
            if (request.RequestUri.Host == "api.unitune.art") await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.NotFound);
        });
        var pending = Create(handler, clock).GetTrackLinksAsync(Source, default);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(6));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("abc123", (await pending)["spotify"]);
        Assert.Equal(new[] { "song.link", "api.unitune.art", "squigly.link" }, handler.Hosts);
    }

    [Fact]
    public async Task OversizedWebResponse_FallsBackToJsonLinks()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "song.link" => Text(new string(' ', 8 * 1024 * 1024 + 1)),
            "api.unitune.art" => Text("""{"linksByPlatform":{"deezer":{"url":"https://deezer.com/track/64"}}}"""),
            _ => new(HttpStatusCode.NotFound)
        }));
        Assert.Equal("64", (await Create(handler).GetTrackLinksAsync(Source, default))["deezer"]);
    }

    [Fact]
    public async Task SharedWebLimit_AppliesAcrossServiceInstances()
    {
        var clock = new ManualTimeProvider();
        const string complete = """
            <a href='https://open.spotify.com/track/abc123'>Spotify</a>
            <a href='https://deezer.com/track/71'>Deezer</a>
            <a href='https://tidal.com/browse/track/42'>Tidal</a>
            <a href='https://open.qobuz.com/track/72'>Qobuz</a>
            <a href='https://music.amazon.com/tracks/B012345678'>Amazon</a>
            <a href='https://music.youtube.com/watch?v=Abc_def-123'>YouTube</a>
            <a href='https://music.apple.com/us/song/73'>Apple</a>
            """;
        var handler = new Handler((_, _) => Task.FromResult(Text(complete)));
        for (var index = 0; index < 20; index++) await Create(handler, clock).GetTrackLinksAsync(Source, default);
        using var cancel = new CancellationTokenSource();
        var pending = Create(handler, clock).GetTrackLinksAsync(Source, cancel.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(6), TimeSpan.FromMinutes(1));
        Assert.Equal(20, handler.Hosts.Count);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(20, handler.Hosts.Count);
    }

    [Fact]
    public async Task FailedTranslation_DoesNotLogKeysOrRequestUrls()
    {
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<OdesliService>>();
        var handler = new Handler((_, _) => throw new HttpRequestException("private-key https://api.song.link/?key=private-key"));
        Assert.Null(await Create(handler, apiKey: "private-key", logger: logger.Object).ConvertTidalToSpotifyIdAsync("42"));
        var entry = Assert.Single(logger.Invocations, call => call.Method.Name == "Log");
        Assert.DoesNotContain("private-key", entry.Arguments[2].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api.song.link", entry.Arguments[2].ToString(), StringComparison.Ordinal);
        Assert.Null(entry.Arguments[3]);
    }

    [Fact]
    public async Task ChainDeadline_KeepsPartialLinksAndStopsFallback()
    {
        var clock = new ManualTimeProvider();
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "song.link") return Task.FromResult(Text(WebLinks));
            clock.Advance(TimeSpan.FromSeconds(12));
            throw new TaskCanceledException("Deadline expired");
        });
        Assert.Equal("abc123", (await Create(handler, clock).GetTrackLinksAsync(Source, default))["spotify"]);
        Assert.Equal(new[] { "song.link", "api.unitune.art" }, handler.Hosts);
    }

    [Fact]
    public async Task Cancellation_DiscardsPartialLinksAndDoesNotCacheThem()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "song.link") return Task.FromResult(Text(WebLinks));
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });
        var service = Create(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetTrackLinksAsync(Source, cancel.Token));
        Assert.Null(await service.TranslateCachedTrackUrlAsync(Source, "spotify"));
    }

    [Fact]
    public async Task Squigly_UsesCreateThenBoundedPagePayload()
    {
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/create")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                return Task.FromResult(Text("""{"full_url":"https://squigly.link/result/example","title":"Fixture","artist":"Example"}"""));
            }
            return Task.FromResult(request.RequestUri.Host == "squigly.link"
                ? Text("""<script>window.__SQUIGLY_LINK__ = {"data":{"services":{"qobuz":{"url":"https://open.qobuz.com/track/63"},"amazonMusic":{"url":"https://music.amazon.com/tracks/B012345678"},"youtubeMusic":{"url":"https://music.youtube.com/watch?v=Abc_def-123"}}}};</script>""")
                : new(HttpStatusCode.NotFound));
        });
        var links = await Create(handler).GetTrackLinksAsync(Source, default);
        Assert.Equal("63", links["qobuz"]);
        Assert.Equal("B012345678", links["amazon"]);
        Assert.Equal("Abc_def-123", links["youtube"]);
    }

    [Theory]
    [InlineData("<a href='https://tidal.com/browse/track/42'>source only</a>")]
    [InlineData("<a href='https://tidal.com/browse/track/99'>wrong</a><a href='https://deezer.com/track/9'>target</a>")]
    [InlineData("<a href='https://deezer.com/track/9'>one</a><a href='https://deezer.com/track/10'>two</a>")]
    public void HtmlParsing_RejectsConflictingTrackIdentities(string html)
    {
        Assert.Throws<OdesliLookupException>(() => PlatformLinkResolverChain.ParseHtml(html, Source, default));
    }

    [Fact]
    public void HtmlParsing_EnforcesByteAndNodeBounds()
    {
        Assert.Throws<OdesliLookupException>(() => PlatformLinkResolverChain.ParseHtml(new string(' ', 8 * 1024 * 1024 + 1), Source, default));
        Assert.Throws<OdesliLookupException>(() => PlatformLinkResolverChain.ParseHtml(string.Concat(Enumerable.Repeat("<i></i>", 100001)), Source, default));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(18)]
    public async Task RateLimit_ReservesDistinctSlotsAndAllowsCancellation(int maximum)
    {
        var clock = new ManualTimeProvider();
        var limiter = new PlatformLinkRateLimiter(maximum, TimeSpan.FromMinutes(1), clock);
        await Task.WhenAll(Enumerable.Range(0, maximum).Select(_ => limiter.WaitAsync(default)));
        using var cancel = new CancellationTokenSource();
        var waiting = limiter.WaitAsync(cancel.Token);
        Assert.Equal(TimeSpan.FromMinutes(1), await clock.NextTimerAsync());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        clock.Advance(TimeSpan.FromMinutes(1));
        await limiter.WaitAsync(default);
    }

    [Fact]
    public async Task MusicBrainzFallback_UsesMetadataFromEarlierServices()
    {
        var clock = new ManualTimeProvider();
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "api.unitune.art" => Text("""{"entityUniqueId":"fixture","entitiesByUniqueId":{"fixture":{"title":"Sample Song","artistName":"Example Artist"}}}"""),
            "musicbrainz.test" => request.RequestUri.AbsolutePath.EndsWith("/recording", StringComparison.Ordinal)
                ? Text("""{"recordings":[{"id":"5cdd7db3-8795-442d-8438-ddb8d7ea16a4","score":97,"title":"Sample Song","artist-credit":[{"name":"Example Artist"}]}]}""")
                : Text("""{"relations":[{"url":{"resource":"https://open.qobuz.com/track/93"}}]}"""),
            _ => new(HttpStatusCode.NotFound)
        }));
        var catalog = new MusicBrainzService(Factory(handler), Options.Create(new MusicBrainzSettings
        {
            Enabled = true,
            BaseUrl = "https://musicbrainz.test/ws/2",
            RateLimitMs = 1000
        }), Options.Create(new CacheSettings()), new TestMemoryApplicationCache(), new(new()),
            NullLogger<MusicBrainzService>.Instance, clock);
        var pending = Create(handler, clock, musicBrainz: catalog).GetTrackLinksAsync(Source, default);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("93", (await pending)["qobuz"]);
        Assert.Contains("Sample Song", Uri.UnescapeDataString(handler.Uris[2].Query), StringComparison.Ordinal);
        Assert.Equal(new[] { "song.link", "api.unitune.art", "musicbrainz.test", "musicbrainz.test", "squigly.link" }, handler.Hosts);
    }

    [Fact]
    public async Task RateLimit_BackoffIsBoundedAndSharedWithWaiters()
    {
        var clock = new ManualTimeProvider();
        var limiter = new PlatformLinkRateLimiter(20, TimeSpan.FromMinutes(1), clock);
        await limiter.BackoffAsync(TimeSpan.FromHours(2), default);
        using var cancel = new CancellationTokenSource();
        var pending = limiter.WaitAsync(cancel.Token);
        Assert.Equal(TimeSpan.FromHours(1), await clock.NextTimerAsync());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        clock.Advance(TimeSpan.FromHours(1));
        await limiter.WaitAsync(default);
    }

    [Fact]
    public async Task MusicBrainzFallback_ReusesCatalogClientAndFollowsRecordingRelations()
    {
        var clock = new ManualTimeProvider();
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "musicbrainz.test"
            ? request.RequestUri.AbsolutePath.Contains("/isrc/", StringComparison.Ordinal)
                ? Text("""{"recordings":[{"id":"5cdd7db3-8795-442d-8438-ddb8d7ea16a4","isrcs":["USABC1234567"]}]}""")
                : Text("""{"relations":[{"url":{"resource":"https://deezer.com/track/92"}}]}""")
            : new(HttpStatusCode.NotFound)));
        var factory = Factory(handler);
        var catalog = new MusicBrainzService(factory, Options.Create(new MusicBrainzSettings
        {
            Enabled = true,
            BaseUrl = "https://musicbrainz.test/ws/2",
            RateLimitMs = 1000
        }), Options.Create(new CacheSettings()), new TestMemoryApplicationCache(), new(new()),
            NullLogger<MusicBrainzService>.Instance, clock);
        var pending = Create(handler, clock, musicBrainz: catalog).GetTrackLinksAsync(Source, default, "USABC1234567");
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("92", (await pending)["deezer"]);
        Assert.Equal(new[] { "song.link", "api.unitune.art", "musicbrainz.test", "musicbrainz.test", "squigly.link" }, handler.Hosts);
        Assert.Contains("url-rels", handler.Uris[3].Query, StringComparison.Ordinal);
    }

    internal static OdesliService Create(HttpMessageHandler handler, TimeProvider? timeProvider = null, string? apiKey = null,
        Microsoft.Extensions.Logging.ILogger<OdesliService>? logger = null, MusicBrainzService? musicBrainz = null)
    {
        CacheExtensions.InitializeCacheSettings(Mock.Of<IServiceProvider>());
        return new(Factory(handler), logger ?? NullLogger<OdesliService>.Instance, new TestMemoryApplicationCache(),
            Options.Create(new OdesliSettings { Enabled = true, RateLimitMs = 1000, ApiKey = apiKey }),
            timeProvider: timeProvider ?? new ManualTimeProvider(), musicBrainz: musicBrainz);
    }

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() =>
        {
            var client = new HttpClient(handler, disposeHandler: false);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "fixture");
            return client;
        });
        return factory.Object;
    }

    private static HttpResponseMessage Text(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<string> Accepts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Hosts.Add(request.RequestUri!.Host);
            Uris.Add(request.RequestUri);
            Accepts.Add(request.Headers.Accept.ToString());
            return respond(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("qobuz", "qobuz", "42", "https://open.qobuz.com/track/42")]
    [InlineData("spotiflac-deezer", "deezer", "3135556", "https://www.deezer.com/track/3135556")]
    [InlineData("spotiflac-apple-music", "apple-musickit", "2037093408", "https://music.apple.com/us/song/2037093408")]
    [InlineData("spotiflac-qobuz-web", "qobuz", "42", "https://open.qobuz.com/track/42")]
    [InlineData("spotiflac-tidal-web", "tidal", "42", "https://tidal.com/browse/track/42")]
    public void OdesliTrackUrl_UsesProviderTrackRoutes(string provider, string normalized, string trackId, string expected)
    {
        Assert.Equal(normalized, OdesliService.NormalizeProviderId(provider));
        Assert.Equal(expected, OdesliService.BuildTrackUrl(provider, trackId));
    }
}
