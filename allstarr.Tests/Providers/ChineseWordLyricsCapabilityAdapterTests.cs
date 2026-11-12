using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class ChineseWordLyricsCapabilityAdapterTests
{
    [Fact]
    public async Task NeteaseRejectsWrongTitleNumbersAndInstrumentalAlbums()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? """
            {"code":200,"result":{"songs":[
            {"id":11,"name":"Part 3","artists":[{"name":"Artist"}],"duration":180000},
            {"id":12,"name":"Part 2","artists":[{"name":"Artist"}],"duration":180000,"album":{"name":"Instrumental"}},
            {"id":13,"name":"Part 2","artists":[{"name":"Artist"}],"duration":180000}
            ]}}
            """ : """{"code":200,"yrc":{"lyric":"[1000,500](1000,500,0)Short"}}"""));
        var request = new ProviderLyricsRequest(Guid.NewGuid(), new("neteaselyrics", ProviderResourceKind.Track, "recording"),
            trackTitle: "Part 2", artistNames: ["Artist"], durationSeconds: 180);
        Assert.Equal(ProviderLyricsAvailabilityState.Available,
            (await Adapter("neteaselyrics", handler).FetchLyricsAsync(Context("neteaselyrics"), request)).RequireValue().Availability);
        Assert.Contains("id=13&", handler.Requests[1].Query);
    }

    [Fact]
    public async Task NeteaseSkipsConflictingCandidatesBeforeFetchingNativeWords()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? """
            {"code":200,"result":{"songs":[
            {"id":11,"name":"Fixture","artists":[{"name":"Other"}],"duration":180000},
            {"id":12,"name":"Fixture","artists":[{"name":"Artist"}],"duration":150000},
            {"id":13,"name":"Fixture","artists":[{"name":"Artist"}],"duration":180000,"album":{"name":"Album"}}
            ]}}
            """ : """{"code":200,"yrc":{"lyric":"[1000,500](1000,500,0)Short"}}"""));
        var value = (await Adapter("neteaselyrics", handler).FetchLyricsAsync(Context("neteaselyrics"), Request("neteaselyrics"))).RequireValue();
        Assert.Equal(ProviderLyricsFormat.WordTimed, value.Format);
        Assert.Equal("[00:01.000]Short", value.Content);
        Assert.Contains("id=13&", handler.Requests[1].Query);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("neteaselyrics", "{\"code\":200,\"result\":{\"songs\":[{\"id\":1,\"name\":\"Fixture (Live)\",\"artists\":[{\"name\":\"Artist\"}],\"duration\":180000}]}}")]
    [InlineData("qqlyrics", "{\"code\":0,\"req_1\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[{\"id\":1,\"name\":\"Fixture\",\"singer\":[{\"name\":\"Other\"}],\"interval\":180,\"album\":{\"name\":\"Album\"}}]}}}}}")]
    [InlineData("kugoulyrics", "{\"status\":200,\"candidates\":[{\"id\":\"1\",\"accesskey\":\"key\",\"song\":\"Fixture\",\"singer\":\"Artist\",\"duration\":150000}]}")]
    public async Task ConflictingSearchIdentityNeverDownloadsLyrics(string provider, string search)
    {
        var handler = new Handler((_, _) => Response(search));
        var result = await Adapter(provider, handler).FetchLyricsAsync(Context(provider), Request(provider));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("USXYZ1234567")]
    [InlineData(null)]
    public async Task KnownDurationAndReturnedIsrcAreRequiredToAgree(string? isrc)
    {
        var song = new { id = 1, name = "Fixture", artists = new[] { new { name = "Artist" } }, duration = isrc == null ? (int?)null : 180000, isrc };
        var handler = new Handler((_, _) => Response(JsonSerializer.Serialize(new { code = 200, result = new { songs = new[] { song } } })));
        var result = await Adapter("neteaselyrics", handler).FetchLyricsAsync(Context("neteaselyrics"), Request("neteaselyrics", isrc: "USABC1234567"));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task KugouSelectsMatchingCandidateAndDecodesRelativeWords()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? """
            {"status":200,"candidates":[
            {"id":"1","accesskey":"bad","song":"Other","singer":"Artist","duration":180000},
            {"id":"2","accesskey":"selected","song":"Fixture","singer":"Artist","duration":180000}
            ]}
            """ : JsonSerializer.Serialize(new { status = 200, content = ChineseWordLyricsParserTests.Krc("[1000,500]<0,500,0>Short") })));
        var value = (await Adapter("kugoulyrics", handler).FetchLyricsAsync(Context("kugoulyrics"), Request("kugoulyrics"))).RequireValue();
        Assert.Equal(ProviderLyricsFormat.WordTimed, value.Format);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(Assert.Single(value.TimedLines!).Segments!).Start);
        Assert.Contains("id=2&accesskey=selected", handler.Requests[1].Query);
    }

    [Fact]
    public async Task QqRequestsEncryptedNativeWordsForTheVerifiedSong()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? QqSearch : JsonSerializer.Serialize(new
        {
            code = 0,
            req_1 = new { code = 0, data = new { songID = 7, songName = "Rml4dHVyZQ==", singerName = "QXJ0aXN0", qrc = 1, crypt = 1, lyric = ChineseWordLyricsParserTests.EncryptedQrc } }
        })));
        var value = (await Adapter("qqlyrics", handler).FetchLyricsAsync(Context("qqlyrics"), Request("qqlyrics"))).RequireValue();
        Assert.Equal(ProviderLyricsFormat.WordTimed, value.Format);
        Assert.Equal("[00:01.000]Short", value.Content);
        var query = Uri.UnescapeDataString(handler.Requests[1].Query[6..]);
        using var data = JsonDocument.Parse(query);
        var parameter = data.RootElement.GetProperty("req_1").GetProperty("param");
        Assert.Equal(7, parameter.GetProperty("songID").GetInt64());
        Assert.Equal(1, parameter.GetProperty("qrc").GetInt32());
        Assert.Equal(1, parameter.GetProperty("crypt").GetInt32());
        Assert.Equal("Rml4dHVyZQ==", parameter.GetProperty("songName").GetString());
    }

    [Fact]
    public async Task QqRejectsAConflictingDownloadedSongIdentifier()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? QqSearch : JsonSerializer.Serialize(new
        {
            code = 0,
            req_1 = new { code = 0, data = new { songID = 99, qrc = 1, crypt = 1, lyric = ChineseWordLyricsParserTests.EncryptedQrc } }
        })));
        var value = (await Adapter("qqlyrics", handler).FetchLyricsAsync(Context("qqlyrics"), Request("qqlyrics"))).RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, value.Availability);
    }

    [Fact]
    public async Task QqMalformedCiphertextFailsTheContract()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? QqSearch : """{"code":0,"req_1":{"code":0,"data":{"qrc":1,"crypt":1,"lyric":"broken"}}}"""));
        var result = await Adapter("qqlyrics", handler).FetchLyricsAsync(Context("qqlyrics"), Request("qqlyrics"));
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Fact]
    public async Task ConflictingLyricMetadataIsUnavailable()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? NeteaseSearch : """
            {"code":200,"yrc":{"lyric":"[ti:Other]\n[ar:Artist]\n[1000,500](1000,500,0)Short"}}
            """));
        var result = await Adapter("neteaselyrics", handler).FetchLyricsAsync(Context("neteaselyrics"), Request("neteaselyrics"));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Fact]
    public async Task NeteaseLineOnlyResponseDoesNotInventWordTiming()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? NeteaseSearch : """{"code":200,"lrc":{"lyric":"[00:01.00]Short"}}"""));
        var result = await Adapter("neteaselyrics", handler).FetchLyricsAsync(Context("neteaselyrics"), Request("neteaselyrics"));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Theory]
    [InlineData("neteaselyrics")]
    [InlineData("qqlyrics")]
    [InlineData("kugoulyrics")]
    public async Task MissingMetadataAndWrongOwnershipNeverSend(string provider)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        var adapter = Adapter(provider, handler);
        var empty = new ProviderLyricsRequest(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, (await adapter.FetchLyricsAsync(Context(provider), empty)).RequireValue().Availability);
        Assert.Equal(ProviderErrorKind.Forbidden, (await adapter.FetchLyricsAsync(Context("other"), Request(provider))).Error!.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("neteaselyrics")]
    [InlineData("qqlyrics")]
    [InlineData("kugoulyrics")]
    public async Task MetadataOnlyLookupRequiresDurationBeforeSending(string provider)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        var request = new ProviderLyricsRequest(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"),
            trackTitle: "Fixture", artistNames: ["Artist"]);
        var result = (await Adapter(provider, handler).FetchLyricsAsync(Context(provider), request)).RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.Availability);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("neteaselyrics", "{\"code\":200,\"result\":[]}")]
    [InlineData("qqlyrics", "{\"code\":0,\"req_1\":{\"code\":0,\"data\":[]}}")]
    [InlineData("kugoulyrics", "{\"status\":200,\"candidates\":{}}")]
    public async Task ChangedSearchShapeFailsTheContract(string provider, string search)
    {
        var result = await Adapter(provider, new Handler((_, _) => Response(search))).FetchLyricsAsync(Context(provider), Request(provider));
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Fact]
    public async Task PlainAndAvailabilityRequestsKeepNativeCacheTiming()
    {
        var handler = new Handler((_, index) => Response(index == 1 ? NeteaseSearch : """{"code":200,"yrc":{"lyric":"[1000,500](1000,500,0)Short"}}"""));
        var adapter = new NetEaseWordLyricsCapabilityAdapter(new HttpClient(handler), new TestMemoryApplicationCache());
        Assert.Null((await adapter.FetchLyricsAsync(Context("neteaselyrics"), Request("neteaselyrics", availability: true))).RequireValue().Content);
        var plain = (await adapter.FetchLyricsAsync(Context("neteaselyrics"), Request("neteaselyrics", format: ProviderLyricsFormat.PlainText))).RequireValue();
        var timed = (await adapter.FetchLyricsAsync(Context("neteaselyrics"), Request("neteaselyrics"))).RequireValue();
        Assert.Equal("Short", plain.Content);
        Assert.Null(plain.TimedLines);
        Assert.NotNull(timed.TimedLines);
        Assert.Equal(2, handler.Requests.Count);
    }

    private const string NeteaseSearch = """{"code":200,"result":{"songs":[{"id":1,"name":"Fixture","artists":[{"name":"Artist"}],"duration":180000}]}}""";
    private const string QqSearch = """{"code":0,"req_1":{"code":0,"data":{"body":{"song":{"list":[{"id":7,"name":"Fixture","singer":[{"name":"Artist"}],"album":{"name":"Album"},"interval":180}]}}}}}""";
    private static IProviderLyricsCapability Adapter(string provider, HttpMessageHandler handler) => provider switch
    {
        "neteaselyrics" => new NetEaseWordLyricsCapabilityAdapter(new HttpClient(handler), null),
        "qqlyrics" => new QqWordLyricsCapabilityAdapter(new HttpClient(handler), null),
        _ => new KugouWordLyricsCapabilityAdapter(new HttpClient(handler), null)
    };
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static ProviderLyricsRequest Request(string provider, string? isrc = null, bool availability = false, ProviderLyricsFormat? format = null) =>
        new(Guid.CreateVersion7(), new(provider, ProviderResourceKind.Track, "lookup"), availability, format, "Fixture", ["Artist"], "Album", 180, isrc);
    private static ProviderExecutionContext Context(string provider) => new(new(ProviderActorKind.PublicRead, null), provider, null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow, true, true, false, [provider]),
        "lyrics-test", "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

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
}
