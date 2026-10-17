using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class UnisonCapabilityAdapterTests
{
    [Theory]
    [InlineData("ttml", "<tt><body><p begin=\"1\"><span begin=\"1\" end=\"2\">Word</span></p></body></tt>", ProviderLyricsFormat.LineTimed)]
    [InlineData("lrc", "[00:01.000]Word", ProviderLyricsFormat.LineTimed)]
    [InlineData("plain", "Word", ProviderLyricsFormat.PlainText)]
    public async Task PublicMetadataLookupPreservesEachSupportedFormat(string format, string lyrics, ProviderLyricsFormat expected)
    {
        var handler = new Handler(new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { success = true, data = new { lyrics, format } }))
        });
        var result = await new UnisonCapabilityAdapter(new HttpClient(handler), null).FetchLyricsAsync(Context(), Request());
        Assert.Equal(expected, result.RequireValue().Format);
        Assert.Equal("https://unison.boidu.dev/lyrics?song=Fixture&artist=Artist&album=Album&duration=180", handler.Request!.AbsoluteUri);
        if (format == "ttml") Assert.Single(Assert.Single(result.RequireValue().TimedLines!).Segments!);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "", null)]
    [InlineData(HttpStatusCode.OK, "{\"success\":false,\"code\":\"NOT_FOUND\"}", null)]
    [InlineData(HttpStatusCode.OK, "[]", ProviderErrorKind.CapabilityUnavailable)]
    [InlineData(HttpStatusCode.OK, "{\"success\":false,\"code\":3}", ProviderErrorKind.CapabilityUnavailable)]
    [InlineData(HttpStatusCode.OK, "{\"success\":true,\"data\":{\"format\":\"html\",\"lyrics\":\"Text\"}}", ProviderErrorKind.CapabilityUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, "", ProviderErrorKind.RateLimited)]
    public async Task MissingUnsupportedAndLimitedResponsesAreTyped(HttpStatusCode status, string content, ProviderErrorKind? error)
    {
        var handler = new Handler(new(status) { Content = new StringContent(content) });
        var result = await new UnisonCapabilityAdapter(new HttpClient(handler), null).FetchLyricsAsync(Context(), Request());
        if (error.HasValue) Assert.Equal(error.Value, result.Error!.Kind);
        else Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Fact]
    public async Task CacheAvoidsRepeatedReadsAndCanceledPacingDoesNotSend()
    {
        var handler = new Handler(new(HttpStatusCode.NotFound));
        var adapter = new UnisonCapabilityAdapter(new HttpClient(handler), new TestMemoryApplicationCache());
        await adapter.FetchLyricsAsync(Context(), Request());
        await adapter.FetchLyricsAsync(Context(), Request());
        Assert.Equal(1, handler.Calls);
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        var request = new ProviderLyricsRequest(Guid.CreateVersion7(), new("unison", ProviderResourceKind.Track, "other"),
            trackTitle: "Other", artistNames: ["Artist"]);
        var result = await adapter.FetchLyricsAsync(Context(cancel.Token), request);
        Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
        Assert.Equal(1, handler.Calls);
    }

    private static ProviderLyricsRequest Request() => new(Guid.CreateVersion7(), new("unison", ProviderResourceKind.Track, "lookup"),
        trackTitle: "Fixture", artistNames: ["Artist"], albumTitle: "Album", durationSeconds: 180);
    private static ProviderExecutionContext Context(CancellationToken token = default) => new(new(ProviderActorKind.PublicRead, null), "unison", null,
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
            true, true, false, ["unison"]), "lyrics-test", "lyrics-test", DateTimeOffset.UtcNow.AddMinutes(1), token);
    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? Request { get; private set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.RequestUri;
            Calls++;
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(response);
        }
    }
}
