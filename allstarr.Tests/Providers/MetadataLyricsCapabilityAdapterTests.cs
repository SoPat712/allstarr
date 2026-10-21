using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed class MetadataLyricsCapabilityAdapterTests
{
    private const string Ttml = "<tt><body><p begin='1.25' end='3'><span begin='1.25' end='2'>Synthetic </span><span begin='2' end='3'>words</span></p></body></tt>";
    private const string Lookup = """
        {"total":1,"source":"HIT-LOCAL-LIBSQL","results":[{"id":"fixture","track_name":"Fixture","artist_name":"Artist","album_name":"Album","duration":180,"isrc":"USABC1234567","timing_type":"word","lyricsUrl":"https://lyrics-storage.binimum.org/USABC1234567.ttml"}]}
        """;

    [Fact]
    public async Task BiniSolvesApiAndStorageIndependentlyAndParsesRenderedLyrics()
    {
        var targets = new List<Uri>();
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler((request, _) =>
        {
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var target = new Uri(payload.RootElement.GetProperty("url").GetString()!);
            targets.Add(target);
            return Response(JsonSerializer.Serialize(new
            {
                status = "ok",
                solution = new
                {
                    url = target.AbsoluteUri,
                    status = 200,
                    response = "<html><head></head><body><pre style=\"word-wrap: break-word; white-space: pre-wrap;\">" +
                        WebUtility.HtmlEncode(target.Host == "lyrics-api.binimum.org" ? Lookup : Ttml) + "</pre></body></html>",
                    headers = new Dictionary<string, string> { ["Content-Type"] = "text/html" },
                    userAgent = "FixtureBrowser/1.0",
                    cookies = Array.Empty<object>()
                }
            }));
        })), _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 60)));
        using var client = new HttpClient(new CloudflareChallengeHandler(solver, ["lyrics-api.binimum.org", "lyrics-storage.binimum.org"])
        {
            InnerHandler = new Handler((request, _) =>
            {
                Assert.Equal(request.RequestUri!.Host == "lyrics-api.binimum.org" ? "application/json" : "application/ttml+xml",
                    Assert.Single(request.Headers.Accept).MediaType);
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new StringContent("") };
                response.Headers.Server.ParseAdd("cloudflare");
                response.Headers.Location = new("/cdn-cgi/challenge-platform/check", UriKind.Relative);
                return response;
            })
        });
        var adapter = new BiniLyricsCapabilityAdapter(client, null, solver: solver);
        var value = (await adapter.FetchLyricsAsync(Context("binilyrics"), Request("binilyrics", isrc: "USABC1234567"))).RequireValue();
        Assert.Equal("[00:01.250]Synthetic words", value.Content);
        Assert.Equal(new[] { "lyrics-api.binimum.org", "lyrics-storage.binimum.org" }, targets.Select(target => target.Host));
    }

    [Fact]
    public async Task BiniContractUsesIsrcThenDownloadsFirstRankedTtmlWithWordTiming()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? Lookup : Ttml));
        var adapter = Adapter("binilyrics", handler, cache: new TestMemoryApplicationCache());
        var probe = (await adapter.FetchLyricsAsync(Context("binilyrics"), Request("binilyrics", isrc: "USABC1234567", availability: true))).RequireValue();
        var value = (await adapter.FetchLyricsAsync(Context("binilyrics"), Request("binilyrics", isrc: "USABC1234567"))).RequireValue();
        Assert.Null(probe.Content);
        Assert.Equal("https://lyrics-api.binimum.org/?isrc=USABC1234567", handler.Requests[0].AbsoluteUri);
        Assert.Equal("https://lyrics-storage.binimum.org/USABC1234567.ttml", handler.Requests[1].AbsoluteUri);
        Assert.Equal("[00:01.250]Synthetic words", value.Content);
        Assert.Equal(ProviderLyricsFormat.LineTimed, value.Format);
        var line = Assert.Single(value.TimedLines!);
        Assert.Equal(2, line.Segments!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), line.Segments[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(750), line.Segments[0].Duration);
        Assert.StartsWith("sha256:", value.Revision);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task BiniEmptyIsrcFallsBackToExactMetadataContractAndLineTiming()
    {
        var handler = new Handler((_, index) => Response(index switch
        {
            1 => "{\"results\":[]}",
            2 => Lookup,
            _ => "<tt><body><p begin='1'>Synthetic line</p></body></tt>"
        }));
        var value = (await Adapter("binilyrics", handler).FetchLyricsAsync(Context("binilyrics"),
            Request("binilyrics", isrc: "USABC1234567"))).RequireValue();
        Assert.Equal("https://lyrics-api.binimum.org/?track=Fixture&artist=Artist&album=Album&duration=180", handler.Requests[1].AbsoluteUri);
        Assert.Equal("[00:01.000]Synthetic line", value.Content);
        Assert.Null(Assert.Single(value.TimedLines!).Segments);
    }

    [Theory]
    [InlineData("Fixture (feat. Guest)", "Artist, Guest", true)]
    [InlineData("Fixture - Live", "Artist", false)]
    [InlineData("Other song", "Artist", false)]
    [InlineData("Fixture", "Other artist", false)]
    public async Task BiniChecksIdentityBeforeFollowingLyricsUrl(string title, string artist, bool accepted)
    {
        var payload = Lookup.Replace("\"track_name\":\"Fixture\"", "\"track_name\":" + JsonSerializer.Serialize(title))
            .Replace("\"artist_name\":\"Artist\"", "\"artist_name\":" + JsonSerializer.Serialize(artist));
        var handler = new Handler((_, index) => Response(index == 1 ? payload : Ttml));
        var result = await Adapter("binilyrics", handler).FetchLyricsAsync(Context("binilyrics"), Request("binilyrics"));
        Assert.Equal(accepted ? ProviderLyricsAvailabilityState.Available : ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Equal(accepted ? 2 : 1, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://lyrics-storage.binimum.org/fixture.ttml")]
    [InlineData("https://127.0.0.1/fixture.ttml")]
    [InlineData("https://lyrics-storage.binimum.org.evil.test/fixture.ttml")]
    [InlineData("https://user:secret@lyrics-storage.binimum.org/fixture.ttml")]
    public async Task BiniRejectsUntrustedDownloadLocations(string url)
    {
        var handler = new Handler((_, _) => Response(Lookup.Replace("https://lyrics-storage.binimum.org/USABC1234567.ttml", url)));
        Assert.False((await Adapter("binilyrics", handler).FetchLyricsAsync(Context("binilyrics"), Request("binilyrics"))).IsSuccess);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GeniusContractKeepsSectionsAndRemovesPageFootersWithoutClaimingTiming()
    {
        var handler = new Handler((_, _) => Response(JsonSerializer.Serialize(new { lyrics = "  [Chorus]\nSynthetic words\n\nLast line\n12Embed\n3 Contributors" })));
        var value = (await Adapter("genius", handler).FetchLyricsAsync(Context("genius"), Request("genius"))).RequireValue();
        Assert.Equal("https://fetch-genius.samidy.workers.dev/?title=Fixture&artist=Artist", Assert.Single(handler.Requests).AbsoluteUri);
        Assert.Equal("[Chorus]\nSynthetic words\n\nLast line", value.Content);
        Assert.Equal(ProviderLyricsFormat.PlainText, value.Format);
        Assert.Null(value.TimedLines);
    }

    [Theory]
    [InlineData("Fixture", "Artist", true)]
    [InlineData("Fixture (feat. Guest)", "Artist, Guest", true)]
    [InlineData("Fixture (Live)", "Artist", false)]
    [InlineData("Fixture", "Another artist", false)]
    public async Task GeniusRejectsConflictingReturnedIdentity(string title, string artist, bool accepted)
    {
        var handler = new Handler((_, _) => Response(JsonSerializer.Serialize(new { title, artist, lyrics = "Synthetic words" })));
        var result = await Adapter("genius", handler).FetchLyricsAsync(Context("genius"), Request("genius"));
        Assert.Equal(accepted ? ProviderLyricsAvailabilityState.Available : ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Theory]
    [InlineData("binilyrics")]
    [InlineData("genius")]
    public async Task MissingMetadataNeverSendsAndWrongOwnerIsForbidden(string provider)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        var adapter = Adapter(provider, handler);
        var empty = new ProviderLyricsRequest(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, (await adapter.FetchLyricsAsync(Context(provider), empty)).RequireValue().Availability);
        Assert.Equal(ProviderErrorKind.Forbidden, (await adapter.FetchLyricsAsync(Context("other"), Request(provider))).Error!.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("binilyrics", 503)]
    [InlineData("binilyrics", 429)]
    [InlineData("genius", 503)]
    [InlineData("genius", 429)]
    public async Task BusyFailuresAreCachedForTenMinutesAndLogsExcludePayloads(string provider, int status)
    {
        var clock = new ManualTimeProvider();
        var logs = new List<string>();
        var logger = new Mock<ILogger>();
        logger.Setup(value => value.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation => logs.Add(invocation.Arguments[2].ToString()!)));
        var handler = new Handler((_, _) => new((HttpStatusCode)status) { Content = new StringContent("Sensitive lyric payload") });
        var adapter = Adapter(provider, handler, clock, logger: logger.Object);
        var first = await adapter.FetchLyricsAsync(Context(provider), Request(provider));
        var second = await adapter.FetchLyricsAsync(Context(provider), Request(provider));
        Assert.False(first.IsSuccess);
        Assert.Equal(first.Error!.Kind, second.Error!.Kind);
        Assert.Single(handler.Requests);
        Assert.NotEmpty(logs);
        Assert.All(logs, line => Assert.DoesNotContain("Sensitive lyric payload", line));
        clock.Advance(TimeSpan.FromMinutes(10));
        await adapter.FetchLyricsAsync(Context(provider), Request(provider));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("binilyrics")]
    [InlineData("genius")]
    public async Task RequestTimeoutIsFourSecondsAndNegativelyCached(string provider)
    {
        var clock = new ManualTimeProvider();
        var handler = new WaitingHandler();
        var adapter = Adapter(provider, handler, clock);
        var pending = adapter.FetchLyricsAsync(Context(provider), Request(provider));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(4));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("lyrics-upstream-busy-or-timed-out", (await pending).Error!.Code);
        Assert.Equal("lyrics-upstream-busy-or-timed-out", (await adapter.FetchLyricsAsync(Context(provider), Request(provider))).Error!.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BiniMultipleRequestsShareSixSecondOverallBudget()
    {
        var clock = new ManualTimeProvider();
        var handler = new WaitingHandler(Lookup, clock);
        var pending = Adapter("binilyrics", handler, clock).FetchLyricsAsync(Context("binilyrics"), Request("binilyrics"));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(4));
        clock.Advance(TimeSpan.FromSeconds(3));
        handler.Release();
        await handler.SecondRequest.Task;
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("lyrics-upstream-busy-or-timed-out", (await pending).Error!.Code);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("binilyrics", "{}")]
    [InlineData("binilyrics", "{\"results\":[null]}")]
    [InlineData("genius", "[]")]
    [InlineData("genius", "{\"lyrics\":42}")]
    public async Task UnexpectedShapesFailSafely(string provider, string payload)
    {
        var result = await Adapter(provider, new Handler((_, _) => Response(payload)))
            .FetchLyricsAsync(Context(provider), Request(provider));
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Fact]
    public async Task BiniUsesOnlyTheFirstRankedResultAndChecksDurationAndIsrc()
    {
        foreach (var payload in new[]
        {
            Lookup.Replace("\"track_name\":\"Fixture\"", "\"track_name\":\"Unrelated\"").Replace("}]}", "}," + Lookup[(Lookup.IndexOf("[{", StringComparison.Ordinal) + 1)..]),
            Lookup.Replace("\"duration\":180", "\"duration\":200"),
            Lookup.Replace("USABC1234567", "GBABC7654321")
        })
        {
            var handler = new Handler((_, _) => Response(payload));
            var result = await Adapter("binilyrics", handler).FetchLyricsAsync(Context("binilyrics"), Request("binilyrics", isrc: "USABC1234567"));
            Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
            Assert.Single(handler.Requests);
        }
    }

    [Theory]
    [InlineData("binilyrics")]
    [InlineData("genius")]
    public async Task SuccessfulLookupLogsExcludeLyricsAndTransportDetails(string provider)
    {
        var logs = new List<string>();
        var logger = new Mock<ILogger>();
        logger.Setup(value => value.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation => logs.Add(invocation.Arguments[2].ToString()!)));
        var handler = new Handler((_, index) => Response(provider == "genius" ? "{\"lyrics\":\"Synthetic private words\"}"
            : index == 1 ? Lookup : Ttml));
        await Adapter(provider, handler, logger: logger.Object).FetchLyricsAsync(Context(provider), Request(provider));
        Assert.Single(logs);
        Assert.Contains(provider, logs[0]);
        Assert.DoesNotContain("Synthetic", logs[0]);
        Assert.DoesNotContain("https:", logs[0]);
    }

    [Theory]
    [InlineData("binilyrics", "{\"results\":[]}")]
    [InlineData("genius", "{\"lyrics\":\"\"}")]
    public async Task CallerCancellationDoesNotPoisonTheFailureCache(string provider, string payload)
    {
        var handler = new CancelOnceHandler(payload);
        var adapter = Adapter(provider, handler);
        using var cancellation = new CancellationTokenSource();
        var pending = adapter.FetchLyricsAsync(Context(provider, cancellation.Token), Request(provider));
        cancellation.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await pending).Error!.Kind);
        Assert.True((await adapter.FetchLyricsAsync(Context(provider), Request(provider))).IsSuccess);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class CancelOnceHandler(string payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Calls == 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response(payload);
        }
    }

    [Fact]
    public async Task BiniNormalizesFeaturedCreditsOnEitherSideOfTheMatch()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? Lookup.Replace("\"artist_name\":\"Artist\"", "\"artist_name\":\"Artist, Guest\"") : Ttml));
        var request = new ProviderLyricsRequest(Guid.CreateVersion7(), new("binilyrics", ProviderResourceKind.Track, "lookup"),
            trackTitle: "Fixture (feat. Guest)", artistNames: ["Artist"]);
        Assert.Equal(ProviderLyricsAvailabilityState.Available,
            (await Adapter("binilyrics", handler).FetchLyricsAsync(Context("binilyrics"), request)).RequireValue().Availability);
    }

    [Fact]
    public async Task GeniusRemovesEmbedWhenItIsAttachedToTheLastLine()
    {
        var handler = new Handler((_, _) => Response("{\"lyrics\":\"[Outro]\\nSynthetic final line12Embed\"}"));
        var result = await Adapter("genius", handler).FetchLyricsAsync(Context("genius"), Request("genius"));
        Assert.Equal("[Outro]\nSynthetic final line", result.RequireValue().Content);
    }

    private static IProviderLyricsCapability Adapter(string provider, HttpMessageHandler handler, TimeProvider? clock = null,
        IApplicationCache? cache = null, ILogger? logger = null) => provider == "binilyrics"
        ? new BiniLyricsCapabilityAdapter(new HttpClient(handler), cache, clock ?? TimeProvider.System, new(clock ?? TimeProvider.System), logger)
        : new GeniusCapabilityAdapter(new HttpClient(handler), cache, clock ?? TimeProvider.System, new(clock ?? TimeProvider.System), logger);
    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private static ProviderLyricsRequest Request(string provider, string? isrc = null, bool availability = false) =>
        new(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"), availability,
            trackTitle: "Fixture", artistNames: ["Artist"], albumTitle: "Album", durationSeconds: 180, isrc: isrc);
    private static ProviderExecutionContext Context(string provider, CancellationToken token = default) => new(new(ProviderActorKind.PublicRead, null), provider, null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, [provider]), "lyrics-test", "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), token);

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

    private sealed class WaitingHandler(string? first = null, TimeProvider? clock = null) : HttpMessageHandler
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public void Release() => release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1 && first != null)
            {
                await release.Task.WaitAsync(cancellationToken);
                return Response(first);
            }
            SecondRequest.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, clock ?? TimeProvider.System, cancellationToken);
            return Response("");
        }
    }
}
