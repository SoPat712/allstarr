using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class LrcRedCapabilityAdapterTests
{
    private const string Line = """
        <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
        <p begin="00:01.234" end="00:03.500">First &amp; second</p>
        <p begin="4.5s" dur="500ms">Last</p>
        </div></body></tt>
        """;
    private const string Words = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div>
        <p begin="1.234" end="3.5"><span begin="1.234" end="2">First</span> <span begin="2" end="3">word</span><span ttm:role="x-bg"><span begin="2.5" end="3.5">(echo)</span></span></p>
        </div></body></tt>
        """;

    [Fact]
    public async Task RichTtmlInheritsPerformerAndKeepsUntimedBackgroundWordsEmpty()
    {
        const string xml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
            <body ttm:agent="solo"><div><p begin="1s" end="3s">Lead<span ttm:role="x-bg">echo</span></p></div></body></tt>
            """;
        var value = (await Adapter(new Handler((_, _) => Response(xml))).FetchLyricsAsync(Context(), Request("USABC1234567"))).RequireValue();
        var rich = allstarr.Core.Music.RichLyricsMapper.Map(new allstarr.Models.Lyrics.LyricsInfo
        { SyncedLyrics = value.Content, TimedLines = value.TimedLines })!;
        Assert.Equal("line", rich.Format);
        Assert.Equal(new[] { "lead", "background" }, rich.Lines.Select(line => line.Part));
        Assert.Equal(new[] { "Lead", "echo" }, rich.Lines.Select(line => line.Text));
        Assert.All(rich.Lines, line =>
        {
            Assert.Empty(line.Words);
            Assert.Equal("v1", line.Voice);
            Assert.Null(line.Singer);
        });
    }

    [Fact]
    public async Task RichTtmlSeparatesPerformersAndOverlappingBackground()
    {
        const string xml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
            <head><metadata><ttm:agent xml:id="voice1"><ttm:name>First singer</ttm:name></ttm:agent>
            <ttm:agent xml:id="voice2"><ttm:name>Second singer</ttm:name></ttm:agent></metadata></head>
            <body><div><p begin="1s" end="3s" ttm:agent="voice1"><span begin="1s" end="2s">Hello </span><span begin="2s" end="3s">world</span><span ttm:role="x-bg" ttm:agent="voice2" begin="1.2s" end="2.4s"><span begin="1.2s" end="1.6s">oh </span><span begin="1.8s" end="2.4s">yes</span></span></p>
            <p begin="4s" end="5s" ttm:agent="voice2">Second</p><p begin="6s" end="7s" ttm:agent="voice1">Again</p></div></body></tt>
            """;
        var value = (await Adapter(new Handler((_, _) => Response(xml))).FetchLyricsAsync(Context(), Request("USABC1234567"))).RequireValue();
        var rich = allstarr.Core.Music.RichLyricsMapper.Map(new allstarr.Models.Lyrics.LyricsInfo
        { Source = value.Source, SyncedLyrics = value.Content, TimedLines = value.TimedLines })!;
        Assert.Equal("word", rich.Format);
        Assert.Equal(new[] { "v1", "v2", "v2", "v1" }, rich.Lines.Select(line => line.Voice));
        Assert.Equal(new[] { "First singer", "Second singer", "Second singer", "First singer" }, rich.Lines.Select(line => line.Singer));
        Assert.Equal("Hello world", rich.Lines[0].Text);
        Assert.Equal((1200L, 2400L, "oh yes", "background"), (rich.Lines[1].StartMs, rich.Lines[1].EndMs, rich.Lines[1].Text, rich.Lines[1].Part));
        Assert.Equal(2, rich.Lines[1].Words.Count);
        Assert.Empty(rich.Lines[2].Words);
        var unnamed = (await Adapter(new Handler((_, _) => Response(xml.Replace("<ttm:name>First singer</ttm:name>", ""))))
            .FetchLyricsAsync(Context(), Request("USABC1234567"))).RequireValue();
        Assert.Equal(value.Revision, unnamed.Revision);
        Assert.Null(unnamed.TimedLines![0].VocalParts![0].Singer);
    }

    [Fact]
    public async Task IsrcSuccessPreservesWordsSpacesBackgroundAndRevision()
    {
        var handler = new Handler((_, _) => Response(Words));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request("USABC1234567"));
        var value = result.RequireValue();
        Assert.Equal("/s/USABC1234567.ttml", Assert.Single(handler.Requests).AbsolutePath);
        Assert.Equal(ProviderLyricsFormat.LineTimed, value.Format);
        Assert.Equal("[00:01.234]First word(echo)", value.Content);
        var line = Assert.Single(value.TimedLines!);
        Assert.Equal(TimeSpan.FromMilliseconds(2266), line.Duration);
        Assert.Equal(new[] { "First ", "word", "(echo)" }, line.Segments!.Select(segment => segment.Text));
        Assert.True(line.Segments![2].IsBackground);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), line.Segments![2].Start);
        Assert.Equal(TimeSpan.FromSeconds(1), line.Segments![2].Duration);
        Assert.Equal("sha256:87beda894f53f01c36fbe84aaa3687a7f5b402477a2d8387c69be8b020d08529", value.Revision);
    }

    [Fact]
    public async Task MissingIsrcFallsBackToMetadataThenDownloadsMatchedIsrc()
    {
        var handler = new Handler((_, index) => index switch
        {
            1 => new(HttpStatusCode.NotFound),
            2 => Response("{\"hits\":[{\"isrc\":\"GBABC7654321\"}]}"),
            _ => Response(Line)
        });
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request("USABC1234567"));
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("https://lrc.red/match.json?track=Fixture&artist=Artist&album=Album&duration=180", handler.Requests[1].AbsoluteUri);
        Assert.Equal("/s/GBABC7654321.ttml", handler.Requests[2].AbsolutePath);
        Assert.Equal("[00:01.234]First & second\n[00:04.500]Last", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines![0].Segments);
    }

    [Fact]
    public async Task IsrcFailureStillTriesMetadataAndEmptyIdentityDoesNotSend()
    {
        var handler = new Handler((_, index) => index switch
        {
            1 => new(HttpStatusCode.ServiceUnavailable),
            2 => Response("{\"hits\":[{\"isrc\":\"GBABC7654321\"}]}"),
            _ => Response(Line)
        });
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request("USABC1234567"));
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(3, handler.Requests.Count);
        var empty = new ProviderLyricsRequest(Guid.CreateVersion7(), new("lrcred", ProviderResourceKind.Track, "lookup"));
        result = await Adapter(handler).FetchLyricsAsync(Context(), empty);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, ProviderErrorKind.PermanentFailure)]
    public async Task IsrcOutcomeIsPreservedWhenArtistIsMissing(HttpStatusCode status, ProviderErrorKind? error)
    {
        var handler = new Handler((_, _) => new(status));
        var request = new ProviderLyricsRequest(Guid.CreateVersion7(), new("lrcred", ProviderResourceKind.Track, "lookup"),
            trackTitle: "Fixture", isrc: "USABC1234567");
        var result = await Adapter(handler).FetchLyricsAsync(Context(), request);
        if (error.HasValue) Assert.Equal(error.Value, result.Error!.Kind);
        else Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Equal("/s/USABC1234567.ttml", Assert.Single(handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task NetworkFailureStillTriesMetadataWithinTheBudget()
    {
        var handler = new Handler((_, index) => index switch
        {
            1 => throw new HttpRequestException("Fixture transport failure"),
            2 => Response("{\"hits\":[{\"isrc\":\"GBABC7654321\"}]}"),
            _ => Response(Line)
        });
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request("USABC1234567"));
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedAndRedirectedResponsesAreRejected(bool redirect)
    {
        var handler = new Handler((_, _) =>
        {
            var response = redirect ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                : Response(new string('x', 2_000_001));
            if (redirect) response.Headers.Location = new Uri("https://other.example/lyrics");
            return response;
        });
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal(redirect ? ProviderErrorKind.PermanentFailure : ProviderErrorKind.CapabilityUnavailable, result.Error!.Kind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"hits\":[null]}")]
    [InlineData("not json")]
    public async Task UnexpectedMatchShapeIsAContractFailure(string json)
    {
        var result = await Adapter(new Handler((_, _) => Response(json))).FetchLyricsAsync(Context(), Request());
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Fact]
    public async Task IsrcOnlyLookupAndWordPreferenceRetainRichTiming()
    {
        var handler = new Handler((_, _) => Response(Words));
        var request = new ProviderLyricsRequest(Guid.CreateVersion7(), new("lrcred", ProviderResourceKind.Track, "lookup"),
            preferredFormat: ProviderLyricsFormat.WordTimed, isrc: "USABC1234567");
        var value = (await Adapter(handler).FetchLyricsAsync(Context(), request)).RequireValue();
        Assert.Equal(ProviderLyricsFormat.LineTimed, value.Format);
        Assert.NotNull(Assert.Single(value.TimedLines!).Segments);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CacheKeepsTimingBeforeAvailabilityAndPlainProjection()
    {
        var handler = new Handler((_, _) => Response(Words));
        var adapter = Adapter(handler, new TestMemoryApplicationCache());
        var probe = await adapter.FetchLyricsAsync(Context(), Request("USABC1234567", availability: true));
        var plain = await adapter.FetchLyricsAsync(Context(), Request("USABC1234567", format: ProviderLyricsFormat.PlainText));
        var timed = await adapter.FetchLyricsAsync(Context(), Request("USABC1234567"));
        Assert.Null(probe.RequireValue().Content);
        Assert.Equal("First word(echo)", plain.RequireValue().Content);
        Assert.Null(plain.RequireValue().TimedLines);
        Assert.NotNull(timed.RequireValue().TimedLines);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{\"hits\":[]}")]
    [InlineData("{\"hits\":[{\"isrc\":\"\"}]}")]
    public async Task NoMatchIsUnavailable(string json)
    {
        var handler = new Handler((_, _) => Response(json));
        var value = (await Adapter(handler).FetchLyricsAsync(Context(), Request())).RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, value.Availability);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("<tt><body>")]
    [InlineData("<tt><body><p begin=\"bad\">Words</p></body></tt>")]
    [InlineData("<tt><body><p begin=\"2\" end=\"1\">Words</p></body></tt>")]
    [InlineData("<tt><body><p begin=\"1\"><span begin=\"-1\">Words</span></p></body></tt>")]
    [InlineData("<tt><body><p begin=\"1\"><span begin=\"2\" end=\"1\">Words</span></p></body></tt>")]
    [InlineData("<tt><body><p begin=\"86401\">Words</p></body></tt>")]
    [InlineData("<!DOCTYPE tt [<!ENTITY x SYSTEM 'file:///nonexistent'>]><tt><body><p begin=\"1\">&x;</p></body></tt>")]
    public async Task MalformedTtmlFailsWithoutUnsafeReads(string xml)
    {
        var handler = new Handler((_, index) => index == 1 ? Response(xml) : Response("{\"hits\":[]}"));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request("USABC1234567"));
        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderError.CompatibilityContractChanged().Kind, result.Error!.Kind);
    }

    [Fact]
    public async Task RateLimitStopsFallbackAndRetainsRetryDelay()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(42));
            return response;
        });
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request("USABC1234567"));
        Assert.Equal(ProviderErrorKind.RateLimited, result.Error!.Kind);
        Assert.Single(handler.Requests);
        Assert.Equal(TimeSpan.FromSeconds(42), result.Error.RetryAfter);
    }

    [Fact]
    public async Task DeadlineAndCallerCancellationStopPendingHttp()
    {
        var handler = new WaitingHandler();
        var adapter = new LrcRedCapabilityAdapter(new HttpClient(handler), null);
        var result = await adapter.FetchLyricsAsync(Context(deadline: DateTimeOffset.UtcNow.AddMilliseconds(100)), Request());
        Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        result = await adapter.FetchLyricsAsync(Context(canceled.Token), Request());
        Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task HttpTimeoutIsTransientAndWrongOwnerNeverSends()
    {
        var handler = new WaitingHandler();
        var adapter = new LrcRedCapabilityAdapter(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) }, null);
        Assert.Equal(ProviderErrorKind.TransientFailure,
            (await adapter.FetchLyricsAsync(Context(), Request())).Error!.Kind);
        Assert.Equal(ProviderErrorKind.Forbidden,
            (await adapter.FetchLyricsAsync(Context(provider: "other"), Request())).Error!.Kind);
        Assert.Equal(1, handler.Calls);
    }

    private static LrcRedCapabilityAdapter Adapter(HttpMessageHandler handler, IApplicationCache? cache = null) =>
        new(new HttpClient(handler), cache);
    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private static ProviderLyricsRequest Request(string? isrc = null, bool availability = false, ProviderLyricsFormat? format = null) =>
        new(Guid.CreateVersion7(), new("lrcred", ProviderResourceKind.Track, "lookup"), availability, format,
            "Fixture", ["Artist"], "Album", 180, isrc);
    private static ProviderExecutionContext Context(CancellationToken token = default, DateTimeOffset? deadline = null, string provider = "lrcred") =>
        new(new(ProviderActorKind.PublicRead, null), provider, null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, false, [provider]), "lyrics-test", "lyrics-test", deadline ?? DateTimeOffset.UtcNow.AddMinutes(1), token);

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(response(request, Requests.Count));
        }
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}
