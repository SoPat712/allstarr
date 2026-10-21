using System.Net;
using System.Collections.Immutable;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("form")]
    [InlineData("json")]
    public async Task SubsonicExternalScrobble_MixedBatchKeepsNativeTimesAndBodyValues(string transport)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var requests = new List<ObservedRequest>();
        using var factory = SubsonicPlaybackFactory("json", recorded, [], observed: requests);
        using var client = factory.CreateClient();
        var body = transport == "form"
            ? "id=ext-deezer-song-42&id=native-track&time=1000&time=2000&submission=false&submission=true&untouched=hello+world"
            : "{\"id\":[\"ext-deezer-song-42\",\"native-track\"],\"time\":[1000,2000],\"submission\":[false,true],\"nested\":{\"keep\":[true,7]}}";
        using var response = await client.PostAsync(SubsonicPlaybackUrl("scrobble", "json"),
            new StringContent(body, Encoding.UTF8, transport == "form" ? "application/x-www-form-urlencoded" : "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(requests).Body!;
        if (transport == "form")
            Assert.Equal("id=native-track&time=2000&submission=true&untouched=hello+world", forwarded);
        else
        {
            using var document = System.Text.Json.JsonDocument.Parse(forwarded);
            Assert.Equal("native-track", document.RootElement.GetProperty("id")[0].GetString());
            Assert.Equal(2000, document.RootElement.GetProperty("time")[0].GetInt32());
            Assert.True(document.RootElement.GetProperty("submission")[0].GetBoolean());
            Assert.True(document.RootElement.GetProperty("nested").GetProperty("keep")[0].GetBoolean());
        }
        Assert.DoesNotContain("ext-deezer", forwarded);
        Assert.Equal(2, recorded.Count);
        Assert.Contains(recorded, signal => signal.ItemId == "native-track" && signal.Transition == PlaybackTransition.Submission);
        Assert.Contains(recorded, signal => signal.ItemId == "ext-deezer-song-42" && signal.Transition == PlaybackTransition.Start);
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    public async Task SubsonicNativeScrobble_AcceptsSuccessAndPreservesBody(string format, bool submission)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        var native = format == "json" ? "{ \"subsonic-response\" : { \"status\" : \"ok\", \"future\" : true } }" :
            "<subsonic-response status=\"ok\" future=\"true\" />";
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed, native);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format) +
            $"&id=native-track&submission={submission.ToString().ToLowerInvariant()}&time=1760000000000");
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        Assert.Equal(submission ? PlaybackTransition.Submission : PlaybackTransition.Start, Assert.Single(recorded).Transition);
        Assert.Single(relayed);
    }

    [Theory]
    [InlineData("json", "Feishin")]
    [InlineData("xml", "Tempo")]
    public async Task SubsonicExternalScrobble_PreservesIdentityAndSubmissionMeaning(string format, string clientName)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed);
        using var client = factory.CreateClient();
        var url = SubsonicPlaybackUrl("scrobble", format, clientName) + "&id=ext-deezer-song-42";
        using var start = await client.GetAsync(url + "&submission=false");
        using var read = await client.GetAsync(SubsonicPlaybackUrl("getNowPlaying", format, clientName));
        var body = await read.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Contains("ext-deezer-song-42", body);
        Assert.Contains("Provider title", body);
        Assert.Contains("native-track", body);
        Assert.Contains(SubsonicNativeNowPlayingEntry(format), body);
        Assert.True(read.Headers.CacheControl?.NoStore);
        var first = Assert.Single(recorded);
        Assert.Equal(PlaybackTransition.Start, first.Transition);
        Assert.Equal("ext-deezer-song-42", first.ItemId);
        using var complete = await client.GetAsync(url + "&submission=true");
        using var retry = await client.GetAsync(url + "&submission=true");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.All(recorded.Skip(1), signal =>
        {
            Assert.Equal(PlaybackTransition.Submission, signal.Transition);
            Assert.Equal(first.PlaySessionId, signal.PlaySessionId);
        });
        Assert.Equal(2, recorded.Select(signal => (signal.Transition, signal.PlaySessionId, signal.ObservedAt)).Distinct().Count());
        Assert.DoesNotContain(relayed, path => path.Contains("scrobble", StringComparison.OrdinalIgnoreCase));
        Assert.All(recorded, signal => Assert.True(signal.ExecutionContext.CanRunUserScopedWork));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicNativeScrobble_RejectsProtocolFailureAndPreservesBody(string format)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        var failure = format == "json" ? "{\"subsonic-response\":{\"status\":\"failed\",\"error\":{\"code\":70}}}" :
            "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"failed\"><error code=\"70\"/></subsonic-response>";
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed, failure);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format) + "&id=native-track&submission=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(failure, await response.Content.ReadAsStringAsync());
        Assert.Empty(recorded);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicNativeReportPlayback_PreservesProtocolResponse(string format)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        var native = format == "json" ? "{ \"subsonic-response\" : { \"status\" : \"ok\", \"future\" : 7 } }" :
            "<subsonic-response status=\"ok\" future=\"7\" />";
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed, native);
        using var client = factory.CreateClient();
        const string body = "mediaId=native-track&mediaType=song&state=starting&positionMs=0&playbackRate=1&ignoreScrobble=true&future=hello+world";
        using var response = await client.PostAsync(SubsonicPlaybackUrl("reportPlayback", format, "Feishin"),
            new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        Assert.Empty(recorded);
        Assert.Single(relayed);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicExternalReportPlayback_ReusesOccurrenceAndHonorsIgnoredScrobbles(string format)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed);
        using var client = factory.CreateClient();
        var url = SubsonicPlaybackUrl("reportPlayback", format, "Feishin");
        foreach (var state in new[] { "starting", "playing", "paused", "playing" })
        {
            using var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["mediaId"] = "ext-deezer-song-42",
                ["mediaType"] = "song",
                ["state"] = state,
                ["positionMs"] = state == "starting" ? "0" : "12000",
                ["playbackRate"] = "1",
                ["ignoreScrobble"] = "true"
            }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var read = await client.GetAsync(SubsonicPlaybackUrl("getNowPlaying", format, "Feishin"));
        Assert.Contains("ext-deezer-song-42", await read.Content.ReadAsStringAsync());
        Assert.NotEmpty(recorded);
        Assert.All(recorded, signal => Assert.False(signal.RelayExternally));
        Assert.Single(recorded.Select(signal => signal.PlaySessionId).Distinct());
        Assert.Contains(recorded, signal => signal.PositionTicks == TimeSpan.FromMilliseconds(12000).Ticks);
        using var stop = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["mediaId"] = "ext-deezer-song-42",
            ["mediaType"] = "song",
            ["state"] = "stopped",
            ["positionMs"] = "12000",
            ["ignoreScrobble"] = "true"
        }));
        using var after = await client.GetAsync(SubsonicPlaybackUrl("getNowPlaying", format, "Feishin"));
        Assert.DoesNotContain("ext-deezer-song-42", await after.Content.ReadAsStringAsync());
        Assert.DoesNotContain(relayed, path => path.Contains("reportPlayback", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicExternalReportPlayback_AndScrobbleShareTheCompletionOccurrence(string format)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed);
        using var client = factory.CreateClient();
        var report = SubsonicPlaybackUrl("reportPlayback", format, "Feishin") +
            "&mediaId=ext-deezer-song-42&mediaType=song&positionMs=0&state=starting&ignoreScrobble=true";
        using var nowPlaying = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format, "Feishin") +
            "&id=ext-deezer-song-42&submission=false");
        using var start = await client.GetAsync(report);
        using var complete = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format, "Feishin") +
            "&id=ext-deezer-song-42&submission=true");
        using var retry = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format, "Feishin") +
            "&id=ext-deezer-song-42&submission=true");
        Assert.Equal(4, recorded.Count);
        Assert.Single(recorded.Select(signal => signal.PlaySessionId).Distinct());
        Assert.True(recorded[0].RelayExternally);
        Assert.False(recorded[1].RelayExternally);
        Assert.All(recorded.Skip(2), signal => Assert.True(signal.RelayExternally));
        Assert.Equal(recorded[^2], recorded[^1] with { ExecutionContext = recorded[^2].ExecutionContext });
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicExternalScrobble_NewStartAfterCompletionCreatesAnotherOccurrence(string format)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed);
        using var client = factory.CreateClient();
        var url = SubsonicPlaybackUrl("scrobble", format, "Tempo") + "&id=ext-deezer-song-42";
        foreach (var suffix in new[]
                 {
                     "&submission=false&time=1760000000000", "&submission=true&time=1760000180000",
                     "&submission=true&time=1760000180000", "&submission=false&time=1760000360000"
                 })
        {
            using var response = await client.GetAsync(url + suffix);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(4, recorded.Count);
        Assert.Equal(recorded[0].PlaySessionId, recorded[1].PlaySessionId);
        Assert.Equal(recorded[1].PlaySessionId, recorded[2].PlaySessionId);
        Assert.NotEqual(recorded[0].PlaySessionId, recorded[3].PlaySessionId);
        Assert.Equal(recorded[1].ObservedAt, recorded[2].ObservedAt);
    }

    [Fact]
    public async Task SubsonicExternalScrobble_RapidNewListenWithoutTimeCreatesAnotherOccurrence()
    {
        var recorded = new List<PlaybackSignalRequest>();
        using var factory = SubsonicPlaybackFactory("json", recorded, []);
        using var client = factory.CreateClient();
        var url = SubsonicPlaybackUrl("scrobble", "json", "Tempo") + "&id=ext-deezer-song-42";
        foreach (var submission in new[] { false, true, true, false })
        {
            using var response = await client.GetAsync(url + "&submission=" + submission.ToString().ToLowerInvariant());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(4, recorded.Count);
        Assert.Equal(recorded[0].PlaySessionId, recorded[1].PlaySessionId);
        Assert.Equal(recorded[1].PlaySessionId, recorded[2].PlaySessionId);
        Assert.NotEqual(recorded[0].PlaySessionId, recorded[3].PlaySessionId);
    }

    [Fact]
    public async Task SubsonicExternalScrobble_NoTimeRetryRetainsAcceptedCompletionAcrossBuckets()
    {
        var recorded = new List<PlaybackSignalRequest>();
        using var factory = SubsonicPlaybackFactory("json", recorded, []);
        using var client = factory.CreateClient();
        var url = SubsonicPlaybackUrl("scrobble", "json", "Tempo") + "&id=ext-deezer-song-42&submission=true";
        using var initial = await client.GetAsync(url);
        var accepted = Assert.Single(recorded);
        var sessions = factory.Services.GetRequiredService<SubsonicPlaybackSessions>();
        var state = Assert.IsType<SubsonicPlaybackState>(sessions.Get(accepted.ExecutionContext));
        var originalTime = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 * 30 - 60);
        sessions.Set(accepted.ExecutionContext, state with
        {
            SubmittedAt = originalTime,
            SubmissionKey = (originalTime.ToUnixTimeSeconds() / 30).ToString()
        });
        using var retry = await client.GetAsync(url);
        using var again = await client.GetAsync(url);
        Assert.Equal(3, recorded.Count);
        Assert.Equal(originalTime, recorded[1].ObservedAt);
        Assert.Equal(accepted.PlaySessionId, recorded[1].PlaySessionId);
        Assert.Equal(recorded[1], recorded[2] with { ExecutionContext = recorded[1].ExecutionContext });
    }

    [Theory]
    [InlineData("json", "missing")]
    [InlineData("json", "null")]
    [InlineData("json", "{}")]
    [InlineData("json", "{\"entry\":null}")]
    [InlineData("json", "{\"entry\":[]}")]
    [InlineData("xml", "missing")]
    [InlineData("xml", "<s:nowPlaying />")]
    public async Task SubsonicExternalNowPlaying_ProjectsEmptyNativeLists(string format, string container)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var native = format == "json"
            ? "{\"subsonic-response\":{\"status\":\"ok\",\"future\":1e+02" +
              (container == "missing" ? "" : ",\"nowPlaying\":" + container) + "}}"
            : "<s:subsonic-response xmlns:s=\"http://subsonic.org/restapi\" status=\"ok\">" +
              (container == "missing" ? "" : container) + "</s:subsonic-response>";
        using var factory = SubsonicPlaybackFactory(format, recorded, [], nowPlayingBody: native);
        using var client = factory.CreateClient();
        using var start = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format) + "&id=ext-deezer-song-42&submission=false");
        using var response = await client.GetAsync(SubsonicPlaybackUrl("getNowPlaying", format));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ext-deezer-song-42", body);
        if (format == "json")
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal(1, document.RootElement.GetProperty("subsonic-response").GetProperty("nowPlaying").GetProperty("entry").GetArrayLength());
            Assert.Contains("\"future\":1e+02", body);
        }
        else
        {
            var document = System.Xml.Linq.XDocument.Parse(body);
            Assert.Single(document.Descendants(), element => element.Name.LocalName == "entry");
            Assert.Contains("<s:subsonic-response xmlns:s=\"http://subsonic.org/restapi\" status=\"ok\">", body);
        }
        Assert.Single(recorded);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicNowPlaying_LeavesNativeBytesAndOtherClientsUnchanged(string format)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed);
        using var client = factory.CreateClient();
        var native = SubsonicNowPlayingBody(format);
        using var before = await client.GetAsync(SubsonicPlaybackUrl("getNowPlaying", format));
        Assert.Equal(native, await before.Content.ReadAsStringAsync());
        using var start = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format, "Tempo") + "&id=ext-deezer-song-42&submission=false");
        using var other = await client.GetAsync(SubsonicPlaybackUrl("getNowPlaying", format, "other"));
        Assert.Equal(native, await other.Content.ReadAsStringAsync());
    }

    private static string SubsonicPlaybackUrl(string method, string format, string clientName = "fixture") =>
        $"/rest/{method}.view?u=fixture&p=secret&v=1.16.1&c={clientName}&f={format}";

    private static string SubsonicNowPlayingBody(string format) => format == "json"
        ? "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\",\"nowPlaying\":{\"entry\":[" + SubsonicNativeNowPlayingEntry(format) + "]}}}"
        : "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\"><nowPlaying>" + SubsonicNativeNowPlayingEntry(format) + "</nowPlaying></subsonic-response>";

    private static string SubsonicNativeNowPlayingEntry(string format) => format == "json"
        ? "{ \"id\" : \"native-track\", \"future\" : 1e+02, \"title\" : \"Native\" }"
        : "<entry title = \"Native\" future=\"1.00\" id = \"native-track\" />";

    private static ProtocolFactory SubsonicPlaybackFactory(string format, List<PlaybackSignalRequest> recorded,
        List<string> relayed, string? reportBody = null, string? nowPlayingBody = null, List<ObservedRequest>? observed = null,
        Action<IServiceCollection>? configure = null)
    {
        var pipeline = new Mock<IPlaybackSignalPipeline>();
        pipeline.Setup(service => service.RecordAsync(It.IsAny<PlaybackSignalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PlaybackSignalRequest, CancellationToken>((signal, _) => recorded.Add(signal)).ReturnsAsync(true);
        var provider = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        provider.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-42",
                Title = "Provider title",
                Artist = "Artist",
                Duration = 180,
                ExternalProvider = "deezer",
                ExternalId = "42"
            });
        return new ProtocolFactory("Subsonic", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/rest/ping.view") return Json(200, "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"}}");
            relayed.Add(path);
            observed?.Add(Observe(request));
            var body = path.Contains("getNowPlaying", StringComparison.OrdinalIgnoreCase) ? nowPlayingBody ?? SubsonicNowPlayingBody(format) :
                reportBody ?? (format == "json" ? "{\"subsonic-response\":{\"status\":\"ok\"}}" : "<subsonic-response status=\"ok\"/>");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, $"application/{format}") };
            response.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=600");
            return response;
        }, services =>
        {
            services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            policies.Setup(service => service.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                    ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                { UserId = user });
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            services.RemoveAll<IPlaybackSignalPipeline>();
            services.AddSingleton(pipeline.Object);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(provider.Object);
            configure?.Invoke(services);
        });
    }
}
