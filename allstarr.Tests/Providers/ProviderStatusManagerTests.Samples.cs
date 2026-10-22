using System.Net;
using allstarr.Core.Health;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed partial class ProviderStatusManagerTests
{
    [Theory]
    [InlineData("sample-unavailable")]
    [InlineData("NotFound")]
    [InlineData("not-found")]
    [InlineData("http-404")]
    [InlineData("http-410")]
    [InlineData("IncompatibleMedia")]
    [InlineData("incompatible-media")]
    public void StreamingMeasurement_MissingSamplesDoNotRecordFailure(string reason)
    {
        var health = new ProviderRuntimeHealth();
        var manager = CreateManager(new Dictionary<string, string?>(), health: health);
        var account = Guid.NewGuid();
        var check = manager.RecordStreamingMeasurement(ProviderCtsDiagnosticResult.Failure(409, "deezer", account, "resolve", reason), 1);
        Assert.NotNull(check);
        Assert.Equal(ProviderHealthState.Unknown, check.Status.Health);
        Assert.Null(health.Get(ProviderRuntimeStatusKey.CreateManaged("deezer", "streaming", account)));
        manager.RestoreCheckResult(ProviderRuntimeStatusKey.CreateManaged("deezer", "streaming", account), 1,
            check.Status with { Health = ProviderHealthState.Degraded, TestedAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal(ProviderHealthState.Unknown, manager.GetManagedStatus("deezer", "streaming", account,
            new Dictionary<string, string> { ["arl"] = "fixture" }, 1).Health);
    }

    [Theory]
    [InlineData("lrclib")]
    [InlineData("lrcred")]
    [InlineData("unison")]
    public async Task LyricsProbe_RemovedRecordingDoesNotFailSource(string provider)
    {
        var calls = 0;
        var handler = new AppleProviderTestFactory.Handler(request =>
        {
            calls++;
            if (provider == "lrcred" && request.RequestUri!.AbsolutePath == "/match.json")
                return Json(HttpStatusCode.OK, "{\"hits\":[]}");
            return calls == 1 ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK,
                provider == "lrcred" ? "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"00:00:01.000\" end=\"00:00:02.000\">Song</p></div></body></tt>" :
                provider == "unison" ? "{\"success\":true,\"data\":{\"lyrics\":\"Song\",\"format\":\"plain\"}}" : "{\"plainLyrics\":\"Song\"}");
        });
        var health = new ProviderRuntimeHealth();
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler), health: health);
        var result = await manager.TestAccountFreeProviderCapabilityAsync(provider, "lyrics");
        Assert.Equal(ProviderHealthState.Healthy, result.Health);
        Assert.InRange(calls, 2, 16);
    }

    [Fact]
    public async Task LyricsProbe_DiscoversAFreshRecordingAfterAllCuratedSamplesDisappear()
    {
        var gets = 0;
        var searches = 0;
        var handler = new AppleProviderTestFactory.Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/search")
            {
                searches++;
                return Json(HttpStatusCode.OK, """[{"trackName":"Fresh song","artistName":"Fresh artist","duration":200}]""");
            }
            gets++;
            return gets <= 8 ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK, """{"plainLyrics":"Words"}""");
        });
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler));
        var result = await manager.TestAccountFreeProviderCapabilityAsync("lrclib", "lyrics");
        Assert.Equal(ProviderHealthState.Healthy, result.Health);
        Assert.Equal(9, gets);
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task LyricsProbe_AllMissingRecordingsRemainNeutral()
    {
        var calls = 0;
        var handler = new AppleProviderTestFactory.Handler(_ => { calls++; return Json(HttpStatusCode.NotFound, "{}"); });
        var manager = CreateManager(new Dictionary<string, string?>(), httpClientFactory: new HandlerHttpClientFactory(handler));
        var result = await manager.TestAccountFreeProviderCapabilityAsync("lrclib", "lyrics");
        Assert.Equal(ProviderHealthState.Unknown, result.Health);
        Assert.Equal("sample-unavailable", result.ReasonCode);
        Assert.False(result.HasCheckResult);
        Assert.Equal(12, calls);
    }
    private static string MusixmatchProbeBody(Uri uri)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            message = new
            {
                header = new { status_code = 200 },
                body = new
                {
                    macro_calls = new Dictionary<string, object>
                    {
                        ["matcher.track.get"] = new
                        {
                            message = new
                            {
                                header = new { status_code = 200 },
                                body = new
                                { track = new { track_id = "42", track_name = query["q_track"].ToString(), artist_name = query["q_artist"].ToString() } }
                            }
                        },
                        ["track.lyrics.get"] = new
                        {
                            message = new
                            {
                                header = new { status_code = 200 },
                                body = new
                                { lyrics = new { lyrics_body = "Words" } }
                            }
                        }
                    }
                }
            }
        });
    }

    private sealed class RepeatingResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
