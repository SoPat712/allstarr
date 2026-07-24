using System.Net;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class SpotifyLyricsServiceTests
{
    [Fact]
    public async Task ConfiguredSidecar_WorksWithoutDirectSpotifyApiOrLocalCookie()
    {
        HttpRequestMessage? observed = null;
        var handler = new StubHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"error":false,"syncType":"LINE_SYNCED","lines":[{"startTimeMs":"1000","words":"Hello","endTimeMs":"2000"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var settings = Options.Create(new SpotifyApiSettings
        {
            Enabled = false,
            SessionCookie = string.Empty,
            LyricsApiUrl = "http://lyrics-sidecar:8080"
        });
        var cache = new DisabledApplicationCache();
        var service = new SpotifyLyricsService(
            NullLogger<SpotifyLyricsService>.Instance,
            settings,
            cache,
            new StubFactory(handler));

        var result = await service.GetLyricsByTrackIdAsync("spotify:track:3yII7UwgLF6K5zW3xad3MP");

        Assert.NotNull(result);
        Assert.Equal("Hello", Assert.Single(result.Lines).Words);
        Assert.Equal(
            "http://lyrics-sidecar:8080/?trackid=3yII7UwgLF6K5zW3xad3MP&format=id3",
            observed?.RequestUri?.AbsoluteUri);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
