using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class LyricsPlusCapabilityAdapterTests
{
    private const string Timed = """
        {"type":"Word","metadata":{"title":"Fixture","artist":"Artist"},
         "lyrics":[{"time":1234,"text":"Synthetic first","syllabus":[{"time":1234,"text":"Synthetic"}]},
                   {"time":2500,"text":"Synthetic second"}]}
        """;

    [Fact]
    public async Task PublicInstancesFallBackAndCacheFullContentBeforeAvailabilityProjection()
    {
        var handler = new Handler((_, index) => index == 1 ? Response(HttpStatusCode.ServiceUnavailable) : Response(HttpStatusCode.OK, Timed));
        var cache = new TestMemoryApplicationCache();
        var adapter = Adapter(handler, cache);
        var probe = await adapter.FetchLyricsAsync(Context(), Request(availability: true));
        var content = await adapter.FetchLyricsAsync(Context(), Request());
        Assert.True(probe.IsSuccess);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, probe.RequireValue().Availability);
        Assert.Null(probe.RequireValue().Content);
        Assert.Equal(ProviderLyricsFormat.LineTimed, content.RequireValue().Format);
        Assert.Equal("[00:01.234]Synthetic first\n[00:02.500]Synthetic second", content.RequireValue().Content);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("lyricsplus.binimum.org", handler.Requests[0].Host);
        Assert.NotEqual(handler.Requests[0].Host, handler.Requests[1].Host);
        Assert.All(handler.Requests, uri =>
        {
            Assert.Equal("/v2/lyrics/get", uri.AbsolutePath);
            var query = Uri.UnescapeDataString(uri.Query);
            Assert.Contains("title=Fixture", query, StringComparison.Ordinal);
            Assert.Contains("artist=Artist", query, StringComparison.Ordinal);
            Assert.Contains("album=Album", query, StringComparison.Ordinal);
            Assert.Contains("duration=180", query, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SelfHostedEndpointPreservesBasePathAndNeverUsesPublicFallback()
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.NotFound));
        var result = await Adapter(handler, endpoint: "http://lyrics:3000/prefix/").FetchLyricsAsync(Context(), Request());
        Assert.True(result.IsSuccess);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Single(handler.Requests);
        Assert.Equal("http://lyrics:3000/prefix/v2/lyrics/get", handler.Requests[0].GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task CacheSeparatesEndpointMetadataAndRequestedFormat()
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK, Timed));
        var cache = new TestMemoryApplicationCache();
        var endpoint = "http://first:3000";
        var adapter = new LyricsPlusCapabilityAdapter(new HttpClient(handler), cache, _ => Task.FromResult(endpoint));
        var timed = await adapter.FetchLyricsAsync(Context(), Request());
        var plain = await adapter.FetchLyricsAsync(Context(), Request(format: ProviderLyricsFormat.PlainText));
        endpoint = "http://second:3000";
        await adapter.FetchLyricsAsync(Context(), Request());
        await adapter.FetchLyricsAsync(Context(), Request(album: "Other album"));
        Assert.Equal(ProviderLyricsFormat.LineTimed, timed.RequireValue().Format);
        Assert.Equal(ProviderLyricsFormat.PlainText, plain.RequireValue().Format);
        Assert.Equal("Synthetic first\nSynthetic second", plain.RequireValue().Content);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{\"type\":\"None\",\"lyrics\":[{\"text\":\"Synthetic plain\"}]}", "Synthetic plain", ProviderLyricsFormat.PlainText)]
    [InlineData("{\"type\":\"Line\",\"lyrics\":[{\"time\":0,\"text\":\"Synthetic start\"}]}", "[00:00.000]Synthetic start", ProviderLyricsFormat.LineTimed)]
    public async Task PlainAndLineSourcesRemainTruthful(string body, string expected, ProviderLyricsFormat format)
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, body)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.RequireValue().Content);
        Assert.Equal(format, result.RequireValue().Format);
        Assert.StartsWith("sha256:", result.RequireValue().Revision);
    }

    [Theory]
    [InlineData("{\"type\":\"Line\",\"lyrics\":[{\"text\":\"Missing time\"}]}")]
    [InlineData("{\"type\":\"Line\",\"lyrics\":[{\"time\":-1,\"text\":\"Negative\"}]}")]
    [InlineData("{\"type\":\"Unknown\",\"lyrics\":[{\"time\":0,\"text\":\"Unknown\"}]}")]
    [InlineData("{\"type\":\"Line\",\"lyrics\":{}}")]
    [InlineData("not json")]
    public async Task MalformedResponsesFailSafely(string body)
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, body)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        Assert.Equal("provider-contract-changed", result.Error?.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task MismatchedMetadataDoesNotReturnLyrics()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK,
                Timed.Replace("\"title\":\"Fixture\"", "\"title\":\"Unrelated recording\""))), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Fact]
    public async Task MultiArtistLookupAcceptsVerifiedPrimaryArtistMetadata()
    {
        var request = new ProviderLyricsRequest(Guid.NewGuid(), new("lyricsplus", ProviderResourceKind.Track, "metadata-key"),
            trackTitle: "Fixture", artistNames: ["Artist", "Guest artist"], albumTitle: "Album", durationSeconds: 180);
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, Timed)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), request);
        Assert.Equal("[00:01.234]Synthetic first\n[00:02.500]Synthetic second", result.RequireValue().Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task PublicMissOrRateLimitFallsBack(HttpStatusCode first)
    {
        var handler = new Handler((_, index) => index == 1 ? Response(first) : Response(HttpStatusCode.OK, Timed));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal("[00:01.234]Synthetic first\n[00:02.500]Synthetic second", result.RequireValue().Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    public async Task SelfHostedFailuresRemainTyped(HttpStatusCode status, ProviderErrorKind kind)
    {
        var result = await Adapter(new Handler((_, _) => Response(status)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        Assert.Equal(kind, result.Error?.Kind);
    }

    [Fact]
    public async Task EmptyTimedLinesAreUnavailable()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK,
                "{\"type\":\"Line\",\"lyrics\":[{\"time\":1234,\"text\":\"\"}]}")), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Fact]
    public async Task DeadlineBoundsACacheThatDoesNotComplete()
    {
        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.IsAny<string>()))
            .Returns(new TaskCompletionSource<ProviderLyricsResult?>().Task);
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK, Timed));
        var result = await Adapter(handler, cache.Object).FetchLyricsAsync(
                Context(deadline: DateTimeOffset.UtcNow.AddSeconds(1)), Request())
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProviderErrorKind.Canceled, result.Error?.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancelingDuringCacheReadCannotReturnCachedContent()
    {
        using var canceled = new CancellationTokenSource();
        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.IsAny<string>())).Returns(() =>
        {
            canceled.Cancel();
            return Task.FromResult<ProviderLyricsResult?>(new(ProviderLyricsAvailabilityState.Available,
                "lyricsplus", ProviderLyricsFormat.PlainText, "Synthetic cached"));
        });
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK, Timed));
        var result = await Adapter(handler, cache.Object).FetchLyricsAsync(Context(canceled.Token), Request());
        Assert.Equal(ProviderErrorKind.Canceled, result.Error?.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://user:password@lyrics:3000")]
    [InlineData("file:///tmp/lyrics")]
    [InlineData("https://lyrics.example/?secret=unsafe")]
    public async Task InvalidConfiguredEndpointCannotReachAnyHost(string endpoint)
    {
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK, Timed));
        var result = await Adapter(handler, endpoint: endpoint).FetchLyricsAsync(Context(), Request());
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error?.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RedirectAndOversizedResponsesAreNotAccepted()
    {
        foreach (var response in new[] { Response(HttpStatusCode.Redirect), Response(HttpStatusCode.OK, new string('x', 2_000_001)) })
        {
            var result = await Adapter(new Handler((_, _) => response), endpoint: "http://lyrics:3000")
                .FetchLyricsAsync(Context(), Request());
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public async Task PolicyAndCancellationAreCheckedBeforeCacheOrHttp()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK, Timed));
        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        var adapter = Adapter(handler, cache.Object);
        Assert.Equal(ProviderErrorKind.Canceled,
            (await adapter.FetchLyricsAsync(Context(canceled.Token), Request())).Error?.Kind);
        Assert.Equal(ProviderErrorKind.Forbidden,
            (await adapter.FetchLyricsAsync(Context(provider: "other"), Request())).Error?.Kind);
        Assert.Empty(handler.Requests);
        cache.VerifyNoOtherCalls();
    }

    private static LyricsPlusCapabilityAdapter Adapter(Handler handler, IApplicationCache? cache = null, string endpoint = "") =>
        new(new HttpClient(handler), cache ?? new TestMemoryApplicationCache(), _ => Task.FromResult(endpoint));

    private static ProviderLyricsRequest Request(bool availability = false, ProviderLyricsFormat? format = null, string album = "Album") =>
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new("lyricsplus", ProviderResourceKind.Track, "metadata-key"),
            availabilityOnly: availability, preferredFormat: format, trackTitle: "Fixture", artistNames: ["Artist"], albumTitle: album, durationSeconds: 180);

    private static ProviderExecutionContext Context(CancellationToken token = default, string provider = "lyricsplus", DateTimeOffset? deadline = null) =>
        new(new(ProviderActorKind.PublicRead, null), provider, null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, false, [provider]), "lyrics", "fixture", deadline ?? DateTimeOffset.UtcNow.AddSeconds(10), token);

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(respond(request, Requests.Count));
        }
    }
}
