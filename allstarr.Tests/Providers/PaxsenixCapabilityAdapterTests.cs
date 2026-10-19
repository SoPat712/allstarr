using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class PaxsenixCapabilityAdapterTests
{
    private const string Match = """{"songmid":"fixture","id":42,"title":"月光","singer":[{"name":"歌手"}],"interval":180}""";

    [Theory]
    [InlineData("\"[00:01.000]<00:01.000>月<00:01.500>光\"", ProviderLyricsFormat.LineTimed)]
    [InlineData("{\"data\":{\"lrc\":{\"lyric\":\"[00:01.000]月光\"}}}", ProviderLyricsFormat.LineTimed)]
    [InlineData("{\"lyrics\":[\"月光\",\"照耀\"]}", ProviderLyricsFormat.PlainText)]
    [InlineData("{\"plainLyrics\":\"月光\"}", ProviderLyricsFormat.PlainText)]
    [InlineData("{\"result\":{\"content\":\"月光\"}}", ProviderLyricsFormat.PlainText)]
    [InlineData("{\"syncedLyrics\":\"[00:01.000]月光\",\"plainLyrics\":\"月光\"}", ProviderLyricsFormat.LineTimed)]
    [InlineData("{\"ttml\":\"<tt><body><p begin='1'><span begin='1' end='2'>月光</span></p></body></tt>\"}", ProviderLyricsFormat.LineTimed)]
    [InlineData("{\"yrc\":{\"lyric\":\"[1000,2000](1000,500,0)月(1500,500,0)光\"}}", ProviderLyricsFormat.LineTimed)]
    [InlineData("{\"krc\":\"[1000,2000]<0,500,0>月<500,500,0>光\"}", ProviderLyricsFormat.LineTimed)]
    [InlineData("{\"krc\":\"[id:fixture]\\n[ar:歌手]\\n[1000,2000]<0,500,0>月<500,500,0>光\"}", ProviderLyricsFormat.LineTimed)]
    public async Task SupportedLyricsShapesPreserveBestTiming(string payload, ProviderLyricsFormat format)
    {
        var handler = new Handler((_, index) => Response(index == 1 ? "[" + Match + "]" : payload));
        var value = (await Adapter(handler).FetchLyricsAsync(Context(), Request())).RequireValue();
        Assert.Equal(format, value.Format);
        Assert.Equal("paxsenix", value.Source);
        Assert.StartsWith("sha256:", value.Revision);
        Assert.Contains("月", value.Content);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/qq/search", handler.Requests[0].AbsolutePath);
        Assert.Contains("id=fixture", handler.Requests[1].Query);
        if (payload.Contains("ttml")) Assert.NotNull(Assert.Single(value.TimedLines!).Segments);
        if (payload.Contains("yrc") || payload.Contains("krc"))
        {
            var words = allstarr.Core.Music.MusicLyrics.ParseSyncedLines(value.Content);
            Assert.Equal("月光", Assert.Single(words).Text);
            Assert.Equal(2, words[0].Cues!.Count);
        }
    }

    [Theory]
    [InlineData("netease", "{\"result\":{\"songs\":[{\"id\":42,\"name\":\"月光\",\"ar\":[{\"name\":\"歌手\"}],\"dt\":180000}]}}", "42")]
    [InlineData("kugou", "{\"data\":{\"tracks\":[{\"hash\":\"fixture-hash\",\"name\":\"月光\",\"artist\":\"歌手\",\"duration\":180}]}}", "fixture-hash")]
    public async Task SearchesOtherCatalogsAndRequestsWordTiming(string catalog, string search, string id)
    {
        var handler = new Handler((uri, _) => Response(uri.AbsolutePath == $"/{catalog}/search" ? search
            : uri.AbsolutePath.EndsWith("/search") ? "[]" : "{\"lyrics\":\"[00:01.000]月光\"}"));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        var lyrics = handler.Requests.Last();
        Assert.Equal($"/{catalog}/lyrics", lyrics.AbsolutePath);
        Assert.Contains("word=true", lyrics.Query);
        Assert.Contains($"id={id}", lyrics.Query);
        Assert.Contains("v=1", lyrics.Query);
    }

    [Theory]
    [InlineData("{\"id\":\"wrong\",\"title\":\"Another song\",\"artist\":\"歌手\"}")]
    [InlineData("{\"id\":\"wrong\",\"title\":\"月光\",\"artist\":\"Someone else\"}")]
    [InlineData("{\"id\":\"wrong\",\"title\":\"月光\",\"artist\":\"歌手\",\"duration\":240}")]
    [InlineData("{\"id\":\"wrong\",\"title\":\"月光 (Live)\",\"artist\":\"歌手\"}")]
    [InlineData("{\"id\":\"wrong\",\"title\":\"月光\"}")]
    [InlineData("{\"id\":\"wrong\",\"title\":\"白月光\",\"artist\":\"歌手\"}")]
    public async Task PoorOrIncompleteMatchesNeverFetchLyrics(string candidate)
    {
        var handler = new Handler((_, _) => Response("[" + candidate + "]"));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.EndsWith("/search", uri.AbsolutePath));
    }

    [Fact]
    public async Task ChoosesBestMatchInsteadOfFirstSearchHit()
    {
        var handler = new Handler((_, index) => Response(index == 1
            ? "[{\"songmid\":\"wrong\",\"title\":\"Other\",\"artist\":\"歌手\"}," + Match + "]"
            : "{\"lyrics\":\"月光\"}"));
        Assert.True((await Adapter(handler).FetchLyricsAsync(Context(), Request())).IsSuccess);
        Assert.Contains("id=fixture", handler.Requests.Last().Query);
    }

    [Fact]
    public async Task MissingLyricsFallsBackAndCacheRetainsRichContentForProjections()
    {
        var handler = new Handler((uri, _) => uri.AbsolutePath.EndsWith("/search") ? Response("[" + Match + "]")
            : uri.AbsolutePath == "/qq/lyrics" ? new(HttpStatusCode.NotFound)
            : Response("{\"lyrics\":\"[00:01.000]<00:01.000>月<00:01.500>光\"}"));
        var adapter = Adapter(handler, new TestMemoryApplicationCache());
        var probe = await adapter.FetchLyricsAsync(Context(), Request(availability: true));
        var plain = await adapter.FetchLyricsAsync(Context(), Request(format: ProviderLyricsFormat.PlainText));
        var timed = await adapter.FetchLyricsAsync(Context(), Request());
        Assert.Null(probe.RequireValue().Content);
        Assert.Equal("月光", plain.RequireValue().Content);
        Assert.Contains("<00:01.500>", timed.RequireValue().Content);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"lyrics\":42}")]
    [InlineData("{\"lyrics\":\"<html>Error</html>\"}")]
    [InlineData("{\"yrc\":\"[1000,2000](bad)月光\"}")]
    public async Task UnsupportedResponsesFailAsContractChanges(string payload)
    {
        var handler = new Handler((_, index) => Response(index == 1 ? "[" + Match + "]" : payload));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal("provider-contract-changed", result.Error!.Code);
    }

    [Fact]
    public async Task MissesAreCachedAndEmptyMetadataNeverSends()
    {
        var handler = new Handler((_, _) => Response("[]"));
        var adapter = Adapter(handler, new TestMemoryApplicationCache());
        await adapter.FetchLyricsAsync(Context(), Request());
        await adapter.FetchLyricsAsync(Context(), Request());
        Assert.Equal(3, handler.Requests.Count);
        var empty = new ProviderLyricsRequest(Guid.CreateVersion7(), new("paxsenix", ProviderResourceKind.Track, "lookup"));
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable,
            (await adapter.FetchLyricsAsync(Context(), empty)).RequireValue().Availability);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.TemporaryRedirect, ProviderErrorKind.PermanentFailure)]
    public async Task RateLimitsAndRedirectsStopFurtherRequests(HttpStatusCode status, ProviderErrorKind error)
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(42));
            if (status == HttpStatusCode.TemporaryRedirect) response.Headers.Location = new("https://other.example/lyrics");
            return response;
        });
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal(error, result.Error!.Kind);
        Assert.Single(handler.Requests);
        if (status == HttpStatusCode.TooManyRequests) Assert.Equal(TimeSpan.FromSeconds(42), result.Error.RetryAfter);
    }

    [Fact]
    public async Task OversizedResponsesAreRejected()
    {
        var handler = new Handler((_, _) => Response(new string('x', 2_000_001)));
        var result = await Adapter(handler).FetchLyricsAsync(Context(), Request());
        Assert.Equal("provider-contract-changed", result.Error!.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SharedPacingSpacesRequestsAcrossInstancesAndCancellationDoesNotSend()
    {
        var clock = new ManualTimeProvider();
        var pacer = new PaxsenixRequestPacer(clock);
        var handler = new Handler((_, _) => new(HttpStatusCode.TooManyRequests));
        var first = new PaxsenixCapabilityAdapter(new HttpClient(handler), null, pacer, clock);
        var second = new PaxsenixCapabilityAdapter(new HttpClient(handler), null, pacer, clock);
        await first.FetchLyricsAsync(Context(), Request());
        using var cancel = new CancellationTokenSource();
        var pending = second.FetchLyricsAsync(Context(cancel.Token), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(2));
        await pending;
        Assert.Equal(2, handler.Requests.Count);
        pending = first.FetchLyricsAsync(Context(cancel.Token), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        Assert.Equal(ProviderErrorKind.Canceled, (await pending).Error!.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RequestTimeoutAndTotalBudgetBoundSlowAndPacedLookups()
    {
        var clock = new ManualTimeProvider();
        var handler = new WaitingHandler();
        var adapter = new PaxsenixCapabilityAdapter(new HttpClient(handler), null,
            new PaxsenixRequestPacer(clock, TimeSpan.Zero), clock);
        var pending = adapter.FetchLyricsAsync(Context(), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(ProviderErrorKind.TransientFailure, (await pending).Error!.Kind);
        Assert.Equal(1, handler.Calls);

        var paced = new Handler((_, _) => Response("[]"));
        adapter = new PaxsenixCapabilityAdapter(new HttpClient(paced), null,
            new PaxsenixRequestPacer(clock, TimeSpan.FromSeconds(20)), clock);
        pending = adapter.FetchLyricsAsync(Context(), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(20));
        clock.Advance(TimeSpan.FromSeconds(18));
        Assert.Equal(ProviderErrorKind.TransientFailure, (await pending).Error!.Kind);
        Assert.Single(paced.Requests);
    }

    private static PaxsenixCapabilityAdapter Adapter(HttpMessageHandler handler, IApplicationCache? cache = null) =>
        new(new HttpClient(handler), cache, new PaxsenixRequestPacer(TimeProvider.System, TimeSpan.Zero), TimeProvider.System);
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static ProviderLyricsRequest Request(bool availability = false, ProviderLyricsFormat? format = null) =>
        new(Guid.CreateVersion7(), new("paxsenix", ProviderResourceKind.Track, "lookup"), availability, format,
            "月光", ["歌手"], durationSeconds: 180);
    private static ProviderExecutionContext Context(CancellationToken token = default) =>
        new(new(ProviderActorKind.PublicRead, null), "paxsenix", null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, true, false, ["paxsenix"]), "lyrics-test", "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), token);

    private sealed class Handler(Func<Uri, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.Equal("lyrics.paxsenix.org", request.RequestUri!.Host);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(response(request.RequestUri!, Requests.Count));
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
