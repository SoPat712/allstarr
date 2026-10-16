using System.Net;
using System.Text;
using System.Text.Json;
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

    private const string TimedWithSegments = """
        {"type":"Word","metadata":{"title":"Fixture","artist":"Artist"},
         "lyrics":[{"time":1234,"duration":1001,"text":"Synthetic first","syllabus":[
             {"time":1234,"duration":321,"text":" Synthetic ","isBackground":true,"synthetic":false},
             {"time":1555,"duration":680,"text":"first","isBackground":false,"synthetic":true}]},
                   {"time":2500,"duration":900,"text":"Synthetic second"}]}
        """;

    private const string TimedWithMalformedSegments = """
        {"type":"Word","metadata":{"title":"Fixture","artist":"Artist"},
         "lyrics":[{"time":1234,"text":"Synthetic first","syllabus":[
             {"time":"not-a-time","duration":321,"text":"first"}]},
                   {"time":2500,"text":"Synthetic second"}]}
        """;

    private const string LineOnly = """
        {"type":"Line","metadata":{"title":"Fixture","artist":"Artist"},
         "lyrics":[{"time":1234,"text":"Synthetic first"},{"time":2500,"text":"Synthetic second"}]}
        """;

    private const string Plain = """
        {"type":"None","metadata":{"title":"Fixture","artist":"Artist"},
         "lyrics":[{"text":"Synthetic first"},{"text":"Synthetic second"}]}
        """;

    private const string LowercaseSyllableType = """
        {"type":"syllable","metadata":{"title":"Fixture","artist":"Artist"},
         "lyrics":[{"time":1234,"text":"Synthetic first","syllabus":[
             {"time":1234,"duration":321,"text":"Synthetic"}]}]}
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

    [Fact]
    public async Task WordTiming_PreservesLineAndSegmentPrecisionFlagsAndSpaces()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, TimedWithSegments)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var value = result.RequireValue();
        Assert.Equal(ProviderLyricsFormat.LineTimed, value.Format);
        Assert.Equal("[00:01.234]Synthetic first\n[00:02.500]Synthetic second", value.Content);
        Assert.NotNull(value.TimedLines);
        var lines = value.TimedLines!;
        Assert.Collection(lines, _ => { }, _ => { });
        var first = lines[0];
        Assert.Equal("Synthetic first", first.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), first.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1001), first.Duration);
        Assert.NotNull(first.Segments);
        var segments = first.Segments!;
        Assert.Collection(segments,
            segment =>
            {
                Assert.Equal(" Synthetic ", segment.Text);
                Assert.Equal(TimeSpan.FromMilliseconds(1234), segment.Start);
                Assert.Equal(TimeSpan.FromMilliseconds(321), segment.Duration);
                Assert.True(segment.IsBackground);
                Assert.False(segment.Synthetic);
            },
            segment =>
            {
                Assert.Equal("first", segment.Text);
                Assert.Equal(TimeSpan.FromMilliseconds(1555), segment.Start);
                Assert.Equal(TimeSpan.FromMilliseconds(680), segment.Duration);
                Assert.False(segment.IsBackground);
                Assert.True(segment.Synthetic);
            });
        Assert.Null(lines[1].Segments);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), lines[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(900), lines[1].Duration);
    }

    [Fact]
    public async Task WordTimedPreferenceStillProjectsLineLrcWithRetainedSegments()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, TimedWithSegments)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request(format: ProviderLyricsFormat.WordTimed));

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
        Assert.Equal("[00:01.234]Synthetic first\n[00:02.500]Synthetic second", result.RequireValue().Content);
        Assert.NotNull(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task LowercaseSyllableTypeIsAdvertisedAsTimed()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, LowercaseSyllableType)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
        Assert.Equal("[00:01.234]Synthetic first", result.RequireValue().Content);
        Assert.NotNull(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task WordTiming_AvailabilityProjectionOmitsTimingButCacheRetainsIt()
    {
        var cache = new TestMemoryApplicationCache();
        var handler = new Handler((_, _) => Response(HttpStatusCode.OK, TimedWithSegments));
        var adapter = Adapter(handler, cache, "http://lyrics:3000");

        var availability = await adapter.FetchLyricsAsync(Context(), Request(availability: true));
        var content = await adapter.FetchLyricsAsync(Context(), Request());

        Assert.True(availability.IsSuccess, availability.Error?.SafeMessage);
        Assert.Null(availability.RequireValue().Content);
        Assert.Null(availability.RequireValue().TimedLines);
        Assert.True(content.IsSuccess, content.Error?.SafeMessage);
        Assert.NotNull(content.RequireValue().TimedLines);
        Assert.Single(handler.Requests);
        Assert.Single(cache.GetKeysByPattern("lyrics:v3:lyricsplus:*"));
        Assert.Empty(cache.GetKeysByPattern("lyrics:v2:lyricsplus:*"));
    }

    [Fact]
    public async Task WordTiming_PlainProjectionOmitsTiming()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, TimedWithSegments)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request(format: ProviderLyricsFormat.PlainText));

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.PlainText, result.RequireValue().Format);
        Assert.Equal("Synthetic first\nSynthetic second", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task WordTiming_InvalidOptionalSegmentsDiscardOnlyTimingVector()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, TimedWithMalformedSegments)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
        Assert.Equal("[00:01.234]Synthetic first\n[00:02.500]Synthetic second", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"bad\"")]
    [InlineData("86400000")]
    public async Task InvalidOptionalLineDurationKeepsValidLineLyrics(string duration)
    {
        var body = TimedWithSegments.Replace("\"duration\":1001", "\"duration\":" + duration, StringComparison.Ordinal);
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, body)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("[00:01.234]Synthetic first\n[00:02.500]Synthetic second", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedOptionalTimingKeepsValidLineLyrics(bool characterBudget)
    {
        var text = characterBudget ? new string('x', 700_000) : "Words";
        var segments = Enumerable.Repeat(new { time = 1234, text }, characterBudget ? 1 : 50_001).ToArray();
        var body = JsonSerializer.Serialize(new { type = "Word", lyrics = new[] { new { time = 1234, text, syllabus = segments } } });
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, 1_999_999);
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, body)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("[00:01.234]" + text, result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task LineOnlyLyricsDoNotInventTimingSegments()
    {
        var result = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, LineOnly)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task TimingOnlyChangesChangeRevisionWhenRenderedLinesStayTheSame()
    {
        var withTiming = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, TimedWithSegments)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        var withoutTiming = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, LineOnly)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());

        Assert.Equal(withTiming.RequireValue().Content, withoutTiming.RequireValue().Content);
        Assert.NotEqual(withTiming.RequireValue().Revision, withoutTiming.RequireValue().Revision);
        Assert.NotNull(withTiming.RequireValue().TimedLines);
        Assert.Null(withoutTiming.RequireValue().TimedLines);
    }

    [Fact]
    public async Task PlainContentRevisionIgnoresMetadataWhenNoTimingExists()
    {
        var first = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK, Plain)), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());
        var second = await Adapter(new Handler((_, _) => Response(HttpStatusCode.OK,
                Plain.Replace("\"artist\":\"Artist\"", "\"artist\":\"Artist\",\"genre\":\"alt\"", StringComparison.Ordinal))), endpoint: "http://lyrics:3000")
            .FetchLyricsAsync(Context(), Request());

        Assert.Equal(first.RequireValue().Content, second.RequireValue().Content);
        Assert.Equal(first.RequireValue().Revision, second.RequireValue().Revision);
        Assert.Null(first.RequireValue().TimedLines);
        Assert.Null(second.RequireValue().TimedLines);
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
            .WaitAsync(TimeSpan.FromSeconds(30));
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
