using allstarr.Services.Common;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class OdesliServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task OptionalUnavailable_IsNonRetryableAndSharedAcrossDistinctUrls(HttpStatusCode status)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status));
        var service = Create(handler);
        var first = await Assert.ThrowsAsync<OdesliLookupException>(() =>
            service.GetTrackLinksAsync("https://tidal.com/browse/track/42", default));
        var second = await Assert.ThrowsAsync<OdesliLookupException>(() =>
            service.GetTrackLinksAsync("https://tidal.com/browse/track/43", default));
        Assert.False(first.Retryable);
        Assert.False(second.Retryable);
        Assert.Equal(1, handler.Calls);
        Assert.Null(await service.TranslateCachedTrackUrlAsync("https://tidal.com/browse/track/44", "spotify"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublicLinks_AreCachedSharedByWrappersAndRejectUntrustedHosts()
    {
        var handler = new Handler(_ => Json("""
            {"linksByPlatform":{
              "tidal":{"url":"https://tidal.com/browse/track/42"},
              "spotify":{"url":"https://open.spotify.com/track/abc123"},
              "deezer":{"url":"https://deezer.com.example.test/track/999"}}}
            """));
        var service = Create(handler);
        var links = await service.GetTrackLinksAsync("https://tidal.com/browse/track/42", default);
        Assert.Equal(2, links.Count);
        Assert.Equal("abc123", await service.ConvertTidalToSpotifyIdAsync("42"));
        Assert.Equal("abc123", await service.ConvertUrlToSpotifyIdAsync("https://tidal.com/browse/track/42"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CrossLinks_RequireTheExactSourceTrackAndCacheNegativeResults()
    {
        var handler = new Handler(_ => Json("""
            {"linksByPlatform":{"tidal":{"url":"https://tidal.com/browse/track/99"},
            "spotify":{"url":"https://open.spotify.com/track/abc123"}}}
            """));
        var service = Create(handler);
        Assert.Empty(await service.GetTrackLinksAsync("https://tidal.com/browse/track/42", default));
        Assert.Empty(await service.GetTrackLinksAsync("https://tidal.com/browse/track/42", default));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Backoff_IsBoundedSharedAndCancellable(HttpStatusCode status)
    {
        var clock = new ManualTimeProvider();
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return response;
        }, clock);
        var service = Create(handler, clock);
        var error = await Assert.ThrowsAsync<OdesliLookupException>(() =>
            service.GetTrackLinksAsync("https://tidal.com/browse/track/42", default));
        Assert.True(error.Retryable);
        Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
        using var cancel = new CancellationTokenSource();
        var pending = service.GetTrackLinksAsync("https://tidal.com/browse/track/43", cancel.Token);
        Assert.Equal(TimeSpan.FromHours(1), await clock.NextTimerAsync());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RequestRate_IsClampedAndWaitCanBeCancelled()
    {
        var clock = new ManualTimeProvider();
        var handler = new Handler(_ => Json("{\"linksByPlatform\":{}}"), clock);
        var service = Create(handler, clock);
        await service.GetTrackLinksAsync("https://tidal.com/browse/track/1", default);
        using var cancel = new CancellationTokenSource();
        var pending = service.GetTrackLinksAsync("https://tidal.com/browse/track/2", cancel.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextTimerAsync());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.GetTrackLinksAsync("https://tidal.com/browse/track/3", default);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(TimeSpan.FromSeconds(1), handler.Times[1] - handler.Times[0]);
    }

    internal static OdesliService Create(HttpMessageHandler handler, TimeProvider? timeProvider = null)
    {
        CacheExtensions.InitializeCacheSettings(Mock.Of<IServiceProvider>());
        var factory = new Mock<IHttpClientFactory>();
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "fixture");
        factory.Setup(item => item.CreateClient("Odesli")).Returns(client);
        return new(factory.Object, NullLogger<OdesliService>.Instance, new TestMemoryApplicationCache(),
            Options.Create(new OdesliSettings { RateLimitMs = 1 }), timeProvider: timeProvider);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TimeProvider? timeProvider = null) : HttpMessageHandler
    {
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
        public int Calls { get; private set; }
        public List<DateTimeOffset> Times { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("api.song.link", request.RequestUri!.Host);
            Calls++;
            Times.Add(_timeProvider.GetUtcNow());
            return Task.FromResult(respond(request));
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
