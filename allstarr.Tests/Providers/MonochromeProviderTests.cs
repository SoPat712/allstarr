using System.Net;
using System.Net.Http.Headers;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Monochrome;
using allstarr.Services.Common;

namespace allstarr.Tests.Providers;

public sealed partial class MonochromeProviderTests
{
    private const string TrackId = "155142501534011392";
    private const string SearchBody = """
        {"tracks":[{"trackId":"155142501534011392","title":"Get Lucky","artistNames":["Daft Punk"],
        "duration":369626,"isrc":"USQX91300108","playable":true}]}
        """;

    [Fact]
    public void Registration_AccountFreeAutomaticStreamingAndTestOperation()
    {
        using var http = Client(_ => Audio());
        var registration = ProviderRegistrationValidator.Validate(MonochromeProviderRegistration.CreateRegistration(new(http)));
        Assert.Equal("monochrome", registration.Descriptor.Id);
        Assert.Equal("Monochrome", registration.Descriptor.DisplayName);
        Assert.Equal(ProviderUseMode.Automatic, registration.Descriptor.DefaultUseMode);
        Assert.All(registration.Descriptor.Capabilities, item => Assert.Equal(ProviderAccountRequirement.None, item.AccountRequirement));
        Assert.DoesNotContain(registration.Descriptor.Capabilities, item => item.Capability == ProviderCapabilityKind.Metadata);
        Assert.Equal("stream", Assert.Single(ProviderTryOperations.Describe(registration.Descriptor, null).Capabilities).Operations.Single().Id);
    }

    [Theory]
    [InlineData(ProviderAudioQuality.Any, true)]
    [InlineData(ProviderAudioQuality.Lossless, true)]
    [InlineData(ProviderAudioQuality.HighResolution, true)]
    [InlineData(ProviderAudioQuality.Lossy, false)]
    [InlineData(ProviderAudioQuality.DataSaver, false)]
    public async Task Stream_UsesNativeIdWithoutQualityQueryAndHonestLosslessFormat(ProviderAudioQuality quality, bool available)
    {
        var calls = 0;
        using var http = Client(request =>
        {
            calls++;
            Assert.Equal($"https://tracks.monochrome.st/track/{TrackId}", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Audio();
        });
        var outcome = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request(quality));
        Assert.Equal(available, outcome.IsSuccess);
        Assert.Equal(available ? 1 : 0, calls);
        if (!available) Assert.Equal(ProviderErrorKind.IncompatibleMedia, outcome.Error!.Kind);
        else
        {
            Assert.Equal("flac", outcome.RequireValue().Media.Container);
            Assert.Equal("flac", outcome.RequireValue().Media.Codec);
            Assert.Equal("audio/flac", outcome.RequireValue().Media.MimeType);
            if (quality == ProviderAudioQuality.HighResolution) Assert.NotNull(outcome.RequireValue().QualityDowngradeReason);
        }
    }

    [Theory]
    [InlineData("258048607")]
    [InlineData("tidal:258048607")]
    [InlineData("../private")]
    public async Task Stream_RejectsTidalAndInvalidIdsWithoutNetwork(string id)
    {
        using var http = Client(_ => throw new InvalidOperationException());
        var outcome = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(),
            new(new("monochrome", ProviderResourceKind.Track, id)));
        Assert.Equal(ProviderErrorKind.NotFound, outcome.Error!.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_ForwardsRangeAndHeadAndDeclaresMeasuredSeekRung(bool ranges)
    {
        var seen = new List<(HttpMethod, string?)>();
        using var http = Client(request =>
        {
            seen.Add((request.Method, request.Headers.Range?.ToString()));
            if (seen.Count == 1) return Audio(ranges);
            if (request.Method == HttpMethod.Head) return new(HttpStatusCode.MethodNotAllowed);
            return Audio(seen.Count == 4 ? false : ranges);
        });
        var lease = (await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request())).RequireValue();
        Assert.Equal(ranges, lease.SupportsByteRanges);
        Assert.Equal(ranges, lease.SupportsSeeking);
        using var get = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        get.Headers.Range = new RangeHeaderValue(4, 9);
        using var audio = await lease.ProtectedResponseFactory!(get, CancellationToken.None);
        using var head = new HttpRequestMessage(HttpMethod.Head, lease.ProtectedSourceUri);
        using var headers = await lease.ProtectedResponseFactory!(head, CancellationToken.None);
        Assert.Equal((HttpMethod.Get, (string?)"bytes=0-0"), seen[0]);
        Assert.Equal((HttpMethod.Get, ranges ? "bytes=4-9" : null), seen[1]);
        Assert.Equal(HttpMethod.Head, seen[2].Item1);
        Assert.Equal((HttpMethod.Get, (string?)null), seen[3]);
        Assert.Equal(HttpStatusCode.OK, headers.StatusCode);
        Assert.Equal(11, headers.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("application/json", "{\"url\":\"https://private.example/audio\"}")]
    [InlineData("application/dash+xml", "<MPD/>")]
    [InlineData("application/vnd.apple.mpegurl", "#EXTM3U")]
    [InlineData("text/html", "<html>unavailable</html>")]
    public async Task Stream_RejectsUndocumentedMediaVariants(string type, string body)
    {
        using var http = Client(_ => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) });
        var outcome = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request());
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, outcome.Error!.Kind);
    }

    [Theory]
    [InlineData(404, ProviderErrorKind.NotFound)]
    [InlineData(410, ProviderErrorKind.NotFound)]
    [InlineData(429, ProviderErrorKind.RateLimited)]
    [InlineData(503, ProviderErrorKind.TransientFailure)]
    [InlineData(401, ProviderErrorKind.Unauthorized)]
    [InlineData(403, ProviderErrorKind.Forbidden)]
    public async Task Stream_MapsStatusAndRetryAfter(int status, ProviderErrorKind expected)
    {
        using var http = Client(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(47));
            return response;
        });
        var outcome = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request());
        Assert.Equal(expected, outcome.Error!.Kind);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(47), outcome.Error.RetryAfter);
    }

    [Theory]
    [InlineData("https://tracks.monochrome.st/track/other", true)]
    [InlineData("https://elsewhere.example/audio", false)]
    [InlineData("http://tracks.monochrome.st/audio", false)]
    [InlineData("https://tracks.monochrome.st:444/audio", false)]
    [InlineData("https://127.0.0.1/audio", false)]
    public async Task Stream_RestrictsRedirectsToReferenceHost(string location, bool allowed)
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            if (++calls != 1) return Audio();
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(location);
            return response;
        });
        var outcome = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request());
        Assert.Equal(allowed, outcome.IsSuccess);
        Assert.Equal(allowed ? 2 : 1, calls);
    }

    [Fact]
    public async Task Probe_OpensFixtureSampleAndReportsFailure()
    {
        var calls = 0;
        using var http = Client(request =>
        {
            Assert.Equal($"/track/{(++calls == 1 ? TrackId : MonochromeStreamingCapabilityAdapter.SampleTrackId)}", request.RequestUri!.AbsolutePath);
            return Audio();
        });
        var adapter = new MonochromeStreamingCapabilityAdapter(http);
        Assert.True((await adapter.ProbeStreamAsync(Context(), Request())).RequireValue().Available);
        Assert.Equal(ProviderProbeStatus.Healthy, (await new MonochromeHealthCapabilityAdapter(adapter)
            .ProbeAsync(Context(), new(ProviderCapabilityKind.Streaming))).RequireValue().Status);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RecordingLookup_UsesOneBoundedSearchAndRequiresExactIsrc(bool exact, bool expected)
    {
        var calls = 0;
        using var http = Client(request =>
        {
            calls++;
            Assert.Equal("/search/tracks", request.RequestUri!.AbsolutePath);
            Assert.Equal("?q=Daft%20Punk%20Get%20Lucky&limit=6", request.RequestUri.Query);
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            return new(HttpStatusCode.OK) { Content = new StringContent(exact ? SearchBody : SearchBody.Replace("USQX91300108", "USRC17607839")) };
        });
        var result = await new MonochromeStreamingCapabilityAdapter(http).ResolveRecordingAsync(Context(),
            new("sample", "Get Lucky", ["Daft Punk"], "USQX91300108", 369626,
                new Dictionary<string, string> { ["spotiflac-tidal-web"] = "tidal:258048607" }), ProviderAudioQuality.Lossless);
        Assert.Equal(1, calls);
        Assert.Equal(expected ? TrackId : null, result.RequireValue().TrackId?.Value);
    }

    [Fact]
    public async Task RecordingLookup_MissingIsrcDoesNotGuessFromTidalId()
    {
        using var http = Client(_ => throw new InvalidOperationException());
        var result = await new MonochromeStreamingCapabilityAdapter(http).ResolveRecordingAsync(Context(),
            new("sample", "Get Lucky", ["Daft Punk"], null, 369626,
                new Dictionary<string, string> { ["tidal"] = "258048607" }), ProviderAudioQuality.Lossless);
        Assert.Null(result.RequireValue().TrackId);
    }

    [Fact]
    public async Task Stream_RateLimitDatePreservesRetryTiming()
    {
        using var http = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2));
            return response;
        });
        var result = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request());
        Assert.InRange(result.Error!.RetryAfter!.Value.TotalSeconds, 118, 120);
    }

    [Fact]
    public async Task Stream_RejectsOversizedAudioWithoutReadingBody()
    {
        using var http = Client(_ =>
        {
            var response = Audio(false);
            response.Content.Headers.ContentLength = 2L * 1024 * 1024 * 1024 + 1;
            return response;
        });
        var result = await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request());
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, result.Error!.Kind);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("invalid")]
    [InlineData("array")]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("unplayable")]
    public async Task RecordingLookup_RejectsUnusableResponses(string shape)
    {
        using var http = Client(_ =>
        {
            var body = shape switch
            {
                "oversized" => new string('x', 256 * 1024 + 1),
                "invalid" => "{",
                "array" => "[]",
                "null" => "null",
                "duplicate" => """{"tracks":[{"trackId":"155142501534011392","isrc":"USQX91300108"},{"trackId":"155142501534011393","isrc":"USQX91300108"}]}""",
                _ => SearchBody.Replace("true", "false", StringComparison.Ordinal)
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var result = await new MonochromeStreamingCapabilityAdapter(http).ResolveRecordingAsync(Context(),
            new("sample", "Get Lucky", ["Daft Punk"], "USQX91300108", null, new Dictionary<string, string>()), ProviderAudioQuality.Any);
        Assert.True(!result.IsSuccess || result.RequireValue().TrackId == null);
    }

    [Fact]
    public async Task RecordingLookup_PreservesNumericNativeIdExactly()
    {
        using var http = Client(_ => new(HttpStatusCode.OK)
        { Content = new StringContent(SearchBody.Replace("\"155142501534011392\"", "155142501534011392", StringComparison.Ordinal)) });
        var result = await new MonochromeStreamingCapabilityAdapter(http).ResolveRecordingAsync(Context(),
            new("sample", "Get Lucky", ["Daft Punk"], "USQX91300108", null, new Dictionary<string, string>()), ProviderAudioQuality.Any);
        Assert.Equal(TrackId, result.RequireValue().TrackId!.Value);
    }

    [Fact]
    public async Task Stream_DisposesResponseWhenBodyCannotOpen()
    {
        var calls = 0;
        var content = new UnavailableBody();
        content.Headers.ContentType = new("audio/flac");
        using var http = Client(_ => ++calls == 1 ? Audio() : new(HttpStatusCode.OK) { Content = content });
        var lease = (await new MonochromeStreamingCapabilityAdapter(http).GetStreamLeaseAsync(Context(), Request())).RequireValue();
        using var outbound = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        await Assert.ThrowsAsync<IOException>(() => lease.ProtectedResponseFactory!(outbound, CancellationToken.None));
        Assert.True(content.Disposed);
    }

    private sealed class UnavailableBody : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new IOException("Fixture body unavailable.");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("Fixture body unavailable.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static ProviderStreamLeaseRequest Request(ProviderAudioQuality quality = ProviderAudioQuality.Lossless) =>
        new(new("monochrome", ProviderResourceKind.Track, TrackId), quality);

    private static ProviderExecutionContext Context() => new(new(ProviderActorKind.SystemJob, null, durableJobId: Guid.NewGuid()), "monochrome", null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, false, false), "monochrome-test", "monochrome-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private static HttpResponseMessage Audio(bool ranges = true)
    {
        var response = new HttpResponseMessage(ranges ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { Content = new ByteArrayContent(ranges ? [102] : "fLaCfixture"u8.ToArray()) };
        response.Content.Headers.ContentType = new("audio/flac");
        if (ranges)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1024);
            response.Headers.AcceptRanges.Add("bytes");
        }
        return response;
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new Handler(handler));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
