using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Moq;

namespace allstarr.Tests;

public sealed partial class MusixmatchLyricsCapabilityAdapterTests
{
    private static readonly Guid RecordingId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountA =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountB =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecretA =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecretB =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string TimedMacroPayload = """
        {
          "message": {
            "header": {
              "status_code": 200
            },
            "body": {
              "macro_calls": {
                "matcher.track.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "track": {
                        "track_id": "track-42",
                        "track_name": "Fixture track",
                        "artist_name": "Fixture artist"
                      }
                    }
                  }
                },
                "track.richsync.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "richsync": {
                        "richsync_body": "[{\"ts\":1.234,\"x\":\"First\"},{\"ts\":2.5,\"x\":\"Second\"}]"
                      }
                    }
                  }
                },
                "track.subtitles.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "subtitle_list": [
                        {
                          "subtitle": {
                            "subtitle_body": "[00:01.234]First\n[00:02.500]Second",
                            "subtitle_language": "en"
                          }
                        }
                      ]
                    }
                  }
                },
                "track.lyrics.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "lyrics": {
                        "lyrics_body": "First\nSecond"
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string MissingMacroPayload =
        """{"message":{"header":{"status_code":200},"body":{"macro_calls":{}}}}""";

    [Theory]
    [InlineData("identity")]
    [InlineData("http-404")]
    [InlineData("matcher-404")]
    [InlineData("envelope-404")]
    public async Task MobileMissDoesNotUseAnUndocumentedSearchFallback(string miss)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var message = node["message"]!["body"]!["macro_calls"]!["matcher.track.get"]!["message"]!;
        if (miss == "identity") message["body"]!["track"]!["track_name"] = "Unrelated recording";
        if (miss == "matcher-404") message["header"]!["status_code"] = 404;
        if (miss == "envelope-404") node["message"]!["header"]!["status_code"] = 404;
        var handler = new FixtureHttpHandler
        {
            Payload = node.ToJsonString(),
            StatusCode = miss == "http-404" ? HttpStatusCode.NotFound : HttpStatusCode.OK
        };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(ProviderLyricsAvailabilityState.Unavailable,
                (await adapter.FetchLyricsAsync(Context(), LyricsRequest())).RequireValue().Availability);

        Assert.Equal(2, handler.Requests);
        Assert.All(handler.RequestUris, uri =>
        {
            Assert.Equal("apic-appmobile.musixmatch.com", uri.Host);
            Assert.Equal("/ws/1.1/macro.subtitles.get", uri.AbsolutePath);
        });
    }

    [Fact]
    public async Task MatcherOmitsUnknownAlbum()
    {
        var handler = new FixtureHttpHandler();
        var request = new ProviderLyricsRequest(RecordingId, new("musixmatch", ProviderResourceKind.Track, "lookup-key"),
            trackTitle: "Fixture track", artistNames: ["Fixture artist"]);

        await CreateAdapter(handler, new FixtureSecretAccessor()).FetchLyricsAsync(Context(), request);

        Assert.False(ParseQuery(handler.RequestUri!).ContainsKey("q_album"));
    }

    [Fact]
    public async Task RejectedIdentityLogsOnlyRequestedAndMatchedSongMetadata()
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var track = node["message"]!["body"]!["macro_calls"]!["matcher.track.get"]!["message"]!["body"]!["track"]!;
        track["track_name"] = "Unrelated recording";
        track["artist_name"] = "Other performer";
        track["album_name"] = "Other album";
        track["commontrack_id"] = 142;
        var logs = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var http = new HttpClient(new FixtureHttpHandler { Payload = node.ToJsonString() });

        await MusixmatchLyricsCapabilityAdapter.ReadLyricsFromTokenAsync(http, "private-token", LyricsRequest(), default,
            factory.CreateLogger<MusixmatchLyricsCapabilityAdapter>());

        var identity = Assert.Single(logs.Messages, entry => entry.Message.Contains("matched-title", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, identity.Level);
        foreach (var value in new[] { "Fixture track", "Fixture artist", "Unrelated recording", "Other performer", "Other album", "42", "142" })
            Assert.Contains(value, identity.Message, StringComparison.Ordinal);
        foreach (var entry in logs.Messages)
            foreach (var value in new[] { "private-token", "Plain fallback", "First", "Second" })
                Assert.DoesNotContain(value, entry.Message, StringComparison.Ordinal);
    }

    private const string RichsyncWithSegmentsBody = """
        [{"ts":1.234,"te":2.750,"x":"First","l":[
          {"c":"Fir","o":0.0},
          {"c":"st ","o":0.125},
          {"c":"word","o":0.321}]},
         {"ts":2.5,"x":"Second","l":[{"c":"Second","o":0.0}]}]
        """;

    private static string PayloadWithRichsync(string richsyncBody, bool compactEncoding = false)
    {
        var node = JsonNode.Parse(TimedMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        calls["track.richsync.get"]!["message"]!["body"]!["richsync"]!["richsync_body"] = richsyncBody;
        return node.ToJsonString(compactEncoding
            ? new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
            : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionHttpClientNeverLogsTheQueryTokenEvenOnTransportFailure(bool fail)
    {
        const string token = "fixture-desktop-token+&=private";
        var logs = new CapturedLogs();
        var handler = new FixtureHttpHandler { ThrowRequestUriInException = fail };
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IApplicationCache>(new TestMemoryApplicationCache());
        services.AddSingleton<IProviderAccountSecretAccessor>(new FixtureSecretAccessor(token));
        services.AddBuiltInLyricsCapabilities();
        services.AddHttpClient(MusixmatchLyricsCapabilityAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient("logging-control").ConfigurePrimaryHttpMessageHandler(() => new FixtureHttpHandler());
        await using var provider = services.BuildServiceProvider();
        using (var control = provider.GetRequiredService<IHttpClientFactory>().CreateClient("logging-control"))
        using (await control.GetAsync("https://logging-control.invalid/probe")) { }
        Assert.Contains(logs.Messages, entry => entry.Category.StartsWith("System.Net.Http.HttpClient.logging-control.", StringComparison.Ordinal));
        logs.Messages.Clear();

        var result = await provider.GetRequiredService<MusixmatchLyricsCapabilityAdapter>()
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.Equal(!fail, result.IsSuccess);
        Assert.Equal(token, ParseQuery(handler.RequestUri!)["usertoken"]);
        Assert.DoesNotContain(logs.Messages, entry => entry.Category.StartsWith("System.Net.Http.HttpClient.MusixmatchLyrics.", StringComparison.Ordinal));
        var text = string.Join('\n', logs.Messages.Select(entry => entry.Message));
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(token), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fixture track", text, StringComparison.Ordinal);
        Assert.DoesNotContain("First", text, StringComparison.Ordinal);
        var diagnostic = Assert.Single(logs.Messages, entry => entry.Category == typeof(MusixmatchLyricsCapabilityAdapter).FullName);
        Assert.Contains("/ws/1.1/macro.subtitles.get", diagnostic.Message);
        Assert.DoesNotContain("?", diagnostic.Message);
        Assert.Contains(fail ? "HTTP (null)" : "HTTP 200", diagnostic.Message);
        if (!fail)
        {
            Assert.Contains("matcher.track.get 200/none", diagnostic.Message);
            Assert.Contains("track.lyrics.get 200/none", diagnostic.Message);
            Assert.Contains("track.subtitles.get 200/none", diagnostic.Message);
            Assert.Contains("track.richsync.get 200/none", diagnostic.Message);
        }
    }

    [Theory]
    [InlineData("Get Lucky", "Daft Punk", "Get Lucky (feat. Pharrell Williams & Nile Rodgers)", "Daft Punk feat. Pharrell Williams & Nile Rodgers", true)]
    [InlineData("Espresso", "Sabrina Carpenter", "Espresso", "Sabrina Carpenter", true)]
    [InlineData("Espresso", "Sabrina Carpenter", "Espresso (Album Version)", "Carpenter, Sabrina", true)]
    [InlineData("Espresso", "Sabrina Carpenter", "Espresso (Radio Edit)", "Sabrina Carpenter", true)]
    [InlineData("Espresso", "Sabrina Carpenter", "Espresso - Live", "Sabrina Carpenter", true)]
    [InlineData("Get Lucky", "Daft Punk", "Around the World", "Daft Punk", false)]
    [InlineData("Get Lucky", "Daft Punk", "Get Lucky Tonight", "Daft Punk", false)]
    [InlineData("Espresso", "Sabrina Carpenter", "Espresso", "Other performer", false)]
    [InlineData("Espresso", "Sabrina Carpenter", "Espresso", "Sabrina Other", false)]
    public async Task MatcherAcceptsNormalizedTitlesAndPrimaryArtistCredits(string title, string artist, string matchedTitle, string matchedArtist, bool accepted)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var track = node["message"]!["body"]!["macro_calls"]!["matcher.track.get"]!["message"]!["body"]!["track"]!;
        track["track_name"] = matchedTitle;
        track["artist_name"] = matchedArtist;
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest(title: title, artist: artist));

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(accepted ? ProviderLyricsAvailabilityState.Available : ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(ProviderLyricsFormat.PlainText, false)]
    [InlineData(ProviderLyricsFormat.PlainText, true)]
    [InlineData(ProviderLyricsFormat.LineTimed, false)]
    [InlineData(ProviderLyricsFormat.LineTimed, true)]
    [InlineData(ProviderLyricsFormat.WordTimed, false)]
    [InlineData(ProviderLyricsFormat.WordTimed, true)]
    public async Task FormatPreferencesKeepAvailableContentThroughCacheAndProjection(ProviderLyricsFormat? preferred, bool plainOnly)
    {
        EnsureCacheSettings();
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        calls.Remove(plainOnly ? "track.subtitles.get" : "track.lyrics.get");
        var handler = new FixtureHttpHandler { Payload = node.ToJsonString() };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());
        var request = LyricsRequest(preferredFormat: preferred);
        var first = await adapter.FetchLyricsAsync(Context(), request);
        var cached = await adapter.FetchLyricsAsync(Context(), request);

        foreach (var result in new[] { first, cached })
        {
            Assert.True(result.IsSuccess, result.Error?.SafeMessage);
            Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
            Assert.Equal(plainOnly ? "Plain fallback" : preferred == ProviderLyricsFormat.PlainText ? "First\nSecond" : "[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
            Assert.Equal(plainOnly || preferred == ProviderLyricsFormat.PlainText ? ProviderLyricsFormat.PlainText : ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
        }
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(30, ProviderLyricsAvailabilityState.Restricted)]
    [InlineData(100, ProviderLyricsAvailabilityState.Available)]
    public async Task PlainPreviewsAreRestrictedAndFullLyricsLoseCommercialFooter(int percentage, ProviderLyricsAvailabilityState expected)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        var lyrics = calls["track.lyrics.get"]!["message"]!["body"]!["lyrics"]!;
        lyrics["lyrics_body"] = "First\nSecond\n\n******* This Lyrics is NOT for Commercial use *******\n(12345678)";
        lyrics["lyrics_body_percentage"] = percentage;
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.Equal(expected, result.RequireValue().Availability);
        Assert.Equal(percentage == 100 ? "First\nSecond" : null, result.RequireValue().Content);
    }

    [Theory]
    [InlineData("selected", "selected:LineTimed", 1, 2, 0)]
    [InlineData("richsync", "selected:LineTimed", 1, 2, 2)]
    [InlineData("text", "selected:PlainText", 1, 0, 0)]
    [InlineData("identity", "identity-rejected", 0, 0, 0)]
    [InlineData("restricted", "restricted", 0, 0, 0)]
    [InlineData("empty", "no-content-parsed", 0, 0, 0)]
    public async Task StructuralDiagnosticsExplainSelectionWithoutResponseValues(string scenario, string decision, int plainLines, int subtitleLines, int richsyncLines)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        var track = calls["matcher.track.get"]!["message"]!["body"]!["track"]!;
        track["private-property-name"] = "private-property-value";
        track["has_lyrics"] = 1;
        track["has_subtitles"] = true;
        track["instrumental"] = 0;
        track["restricted"] = scenario == "restricted";
        if (scenario == "identity") track["track_name"] = "Different song";
        if (scenario == "richsync")
            calls["track.richsync.get"] = JsonNode.Parse(TimedMacroPayload)!["message"]!["body"]!["macro_calls"]!["track.richsync.get"]!.DeepClone();
        if (scenario == "text")
            calls["track.subtitles.get"]!["message"]!["body"]!["subtitle_list"]![0]!["subtitle"]!["subtitle_body"] = "private-subtitle-text";
        if (scenario == "empty")
        {
            calls["track.lyrics.get"]!["message"]!["body"]!["lyrics"]!["lyrics_body"] = "";
            calls["track.subtitles.get"]!["message"]!["body"]!["subtitle_list"] = new JsonArray();
        }
        var logs = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var http = new HttpClient(new FixtureHttpHandler { Payload = node.ToJsonString() });
        await MusixmatchLyricsCapabilityAdapter.ReadLyricsFromTokenAsync(http, "private-token", LyricsRequest(), default,
            factory.CreateLogger<MusixmatchLyricsCapabilityAdapter>());

        var log = logs.Messages.First(entry => entry.Message.Contains("matcher-body", StringComparison.Ordinal)).Message;
        Assert.Contains("matcher-body object; track True; track-keys track_id,track_name,artist_name,has_lyrics,has_subtitles,instrumental,restricted", log);
        Assert.Contains("title-score ", log);
        Assert.Contains("artist-score 100", log);
        Assert.Contains("has-lyrics True; has-subtitles True; instrumental False", log);
        Assert.Contains("lyrics-body object; lyrics-body-present True", log);
        Assert.Contains($"subtitle-body object; subtitle-count {(scenario == "empty" ? 0 : 1)}; subtitle-first {(scenario == "empty" ? "empty" : scenario == "text" ? "text" : "[")}", log);
        Assert.Contains(scenario == "richsync" ? "richsync-body object; richsync-body-present True" : "richsync-body array; richsync-body-present False", log);
        Assert.Contains($"parsed-lines plain {plainLines}/subtitle {subtitleLines}/richsync {richsyncLines}; decision {decision}", log);
        foreach (var secret in new[] { "private-property-name", "private-property-value", "private-token", "Fixture track", "Fixture artist", "Different song", "Plain fallback", "First", "private-subtitle-text" })
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "array")]
    [InlineData("{}", "empty")]
    [InlineData("null", "empty")]
    public async Task StructuralDiagnosticsExposeMissingMatcherTrack(string body, string kind)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        node["message"]!["body"]!["macro_calls"]!["matcher.track.get"]!["message"]!["body"] = JsonNode.Parse(body);
        var logs = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var http = new HttpClient(new FixtureHttpHandler { Payload = node.ToJsonString() });
        await MusixmatchLyricsCapabilityAdapter.ReadLyricsFromTokenAsync(http, "fixture-token", LyricsRequest(), default,
            factory.CreateLogger<MusixmatchLyricsCapabilityAdapter>());
        var log = Assert.Single(logs.Messages).Message;
        Assert.Contains($"matcher-body {kind}; track False; track-keys none", log);
        Assert.Contains("decision no-content-parsed", log);
    }

    private static string DesktopMacroPayload => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Musixmatch", "desktop-macro.json"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopSubtitleShapePreservesMillisecondsWithoutAvailabilityFlags(bool whitespace)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var subtitle = node["message"]!["body"]!["macro_calls"]!["track.subtitles.get"]!["message"]!["body"]!["subtitle_list"]![0]!["subtitle"]!;
        if (whitespace) subtitle["subtitle_body"] = subtitle["subtitle_body"]!.GetValue<string>().Replace("[{", "[ \r\n {", StringComparison.Ordinal);
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
    }

    [Theory]
    [InlineData("richsync")]
    [InlineData("subtitle")]
    [InlineData("plain")]
    public async Task DesktopMacroPrefersRichsyncThenSubtitlesThenPlain(string selected)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        if (selected == "richsync")
            calls["track.richsync.get"] = JsonNode.Parse(TimedMacroPayload.Replace("First", "Rich first", StringComparison.Ordinal))!["message"]!["body"]!["macro_calls"]!["track.richsync.get"]!.DeepClone();
        if (selected == "plain")
        {
            calls["track.subtitles.get"]!["message"]!["header"]!["status_code"] = 404;
            calls["track.subtitles.get"]!["message"]!["body"] = new JsonArray();
        }
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(selected == "plain" ? "Plain fallback" : selected == "richsync" ? "[00:01.234]Rich first\n[00:02.500]Second" : "[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
        Assert.Equal(selected == "plain" ? ProviderLyricsFormat.PlainText : ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
    }

    public static IEnumerable<object[]> AuthorizationCases()
    {
        foreach (var call in new[] { "envelope", "matcher.track.get", "track.lyrics.get", "track.subtitles.get", "track.richsync.get" })
            foreach (var header in new[] { (401, ""), (402, "renew"), (200, "renew"), (200, "captcha"), (404, "captcha") })
                yield return [call, header.Item1, header.Item2];
    }

    [Theory]
    [MemberData(nameof(AuthorizationCases))]
    public async Task AuthorizationFailuresTakePrecedenceOverMissingMatchesAndFallbackLyrics(string call, int status, string hint)
    {
        var node = JsonNode.Parse(TimedMacroPayload)!;
        var message = call == "envelope" ? node["message"]! : node["message"]!["body"]!["macro_calls"]![call]!["message"]!;
        message["header"]!["status_code"] = status;
        message["header"]!["hint"] = hint;
        message["body"] = new JsonArray();
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsReauthentication, result.Error?.Kind);
        Assert.Contains("Reconnect", result.Error?.SafeMessage);
    }

    [Fact]
    public async Task MatcherNotFoundWithArrayBodyIsAnUnavailableMiss()
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var message = node["message"]!["body"]!["macro_calls"]!["matcher.track.get"]!["message"]!;
        message["header"]!["status_code"] = 404;
        message["body"] = new JsonArray();
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest());
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, result.RequireValue().Availability);
    }

    [Theory]
    [InlineData("restricted", false)]
    [InlineData("restricted", true)]
    [InlineData("lyrics_dmca", false)]
    public async Task RestrictedLyricsDoNotEscapeThroughTimedFallbacks(string flag, bool boolean)
    {
        var node = JsonNode.Parse(TimedMacroPayload)!;
        node["message"]!["body"]!["macro_calls"]!["track.lyrics.get"]!["message"]!["body"]!["lyrics"]![flag] = boolean ? JsonValue.Create(true) : JsonValue.Create(1);
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(), LyricsRequest());
        Assert.Equal(ProviderLyricsAvailabilityState.Restricted, result.RequireValue().Availability);
        Assert.Null(result.RequireValue().Content);
    }

    [Fact]
    public async Task MatcherRequestIncludesNormalizedIsrcWithoutInventingSpotifyIdentity()
    {
        var handler = new FixtureHttpHandler { Payload = DesktopMacroPayload };
        var request = new ProviderLyricsRequest(RecordingId, new("musixmatch", ProviderResourceKind.Track, "lookup-key"),
            trackTitle: "Fixture track", artistNames: ["Fixture artist"], isrc: "us-abc-12-34567");
        await CreateAdapter(handler, new FixtureSecretAccessor()).FetchLyricsAsync(Context(), request);
        var query = ParseQuery(handler.RequestUri!);
        Assert.Equal("USABC1234567", query["q_track_isrc"]);
        Assert.False(query.ContainsKey("track_spotify_id"));
        Assert.False(query.ContainsKey("q_duration"));
        Assert.False(query.ContainsKey("f_subtitle_length"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestPanelShowsReconnectOrRestrictionInsteadOfNotFound(bool restricted)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var message = node["message"]!["body"]!["macro_calls"]!["track.lyrics.get"]!["message"]!;
        if (restricted) message["body"]!["lyrics"]!["restricted"] = 1;
        else message["header"]!["hint"] = "renew";
        var adapter = CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor());
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>("musixmatch", ProviderCapabilityKind.Lyrics)).Returns(adapter);
        var result = await new ProviderTryOperations(registry.Object).ExecuteAsync(Context(), new()
        { Capability = "lyrics", Operation = "lyrics", Title = "Fixture track", Artist = "Fixture artist" });

        Assert.False(result.Succeeded);
        Assert.Equal(restricted ? "lyrics-restricted" : "account-needs-reauthentication", result.ReasonCode);
        Assert.Contains(restricted ? "restricted" : "Reconnect", result.Message);
    }

    [Theory]
    [InlineData(200, "renew")]
    [InlineData(401, "captcha")]
    [InlineData(404, "")]
    [InlineData(200, "fixture-private-value")]
    public async Task DiagnosticsReportMacroStatusesAndOnlyKnownHints(int status, string hint)
    {
        var node = JsonNode.Parse(DesktopMacroPayload)!;
        var message = node["message"]!["body"]!["macro_calls"]!["matcher.track.get"]!["message"]!;
        message["header"]!["status_code"] = status;
        message["header"]!["hint"] = hint;
        var logs = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var http = new HttpClient(new FixtureHttpHandler { Payload = node.ToJsonString() });
        var adapter = new MusixmatchLyricsCapabilityAdapter(http, new FixtureSecretAccessor("fixture-private-value"),
            new TestMemoryApplicationCache(), factory.CreateLogger<MusixmatchLyricsCapabilityAdapter>());
        await adapter.FetchLyricsAsync(Context(), LyricsRequest());

        var log = Assert.Single(logs.Messages, entry => entry.Message.Contains("stage matcher", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Contains($"matcher.track.get {status}/{(hint.Length == 0 ? "none" : hint == "fixture-private-value" ? "other" : hint)}", log.Message);
        Assert.Contains("track.richsync.get 404/none", log.Message);
        Assert.DoesNotContain("fixture-private-value", log.Message);
        Assert.DoesNotContain("Fixture track", log.Message);
        Assert.DoesNotContain("Plain fallback", log.Message);
    }

    [Fact]
    public void Registration_AllowsAutomaticTokenAndBothAccountScopes()
    {
        var adapter = CreateAdapter(new FixtureHttpHandler(), new FixtureSecretAccessor());
        var registration = MusixmatchLyricsCapabilityAdapter.CreateRegistration(adapter);
        var validated = ProviderRegistrationValidator.Validate(registration);
        var descriptor = validated.Descriptor;
        var lyrics = Assert.Single(descriptor.Capabilities);
        var token = Assert.Single(descriptor.Settings);

        Assert.Equal(MusixmatchLyricsCapabilityAdapter.StableProviderId, descriptor.Id);
        Assert.Equal(ProviderAccountRequirement.Optional, lyrics.AccountRequirement);
        Assert.Contains(ProviderAccountScope.Personal, lyrics.AllowedAccountScopes);
        Assert.Contains(ProviderAccountScope.Shared, lyrics.AllowedAccountScopes);
        Assert.Contains("fetchLyrics", lyrics.Hooks);
        Assert.Contains(
            descriptor.Permissions.NetworkOrigins,
            origin => origin == new Uri("https://apic-appmobile.musixmatch.com/"));
        Assert.Equal(["userToken"], descriptor.Permissions.SecretSettingKeys);
        Assert.False(descriptor.Permissions.Cache);
        Assert.Equal("userToken", token.Key);
        Assert.Equal(ProviderSettingValueKind.Secret, token.ValueKind);
        Assert.False(token.Required);
        Assert.Equal(
            "Paste the Debug Info from the Musixmatch app's debug info, or just the user token. Optional: Musixmatch works without it.",
            token.HelpText);
        Assert.False(lyrics.SupportsPublicRead);
    }

    [Fact]
    public async Task TimedMacro_UsesExactAccountRequestAndPreservesMilliseconds()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor("desktop-user-token");
        var adapter = CreateAdapter(handler, secrets);

        var outcome = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(preferredFormat: ProviderLyricsFormat.LineTimed));

        Assert.True(outcome.IsSuccess, outcome.Error?.SafeMessage);
        var result = outcome.RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.Availability);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.Format);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.Content);
        Assert.StartsWith("sha256:", result.Revision, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("apic-appmobile.musixmatch.com", handler.RequestUri!.Host);
        Assert.Equal(
            "/ws/1.1/macro.subtitles.get",
            handler.RequestUri.AbsolutePath);

        var query = ParseQuery(handler.RequestUri);
        Assert.Equal("mac-ios-v2.0", query["app_id"]);
        Assert.Equal("json", query["format"]);
        Assert.Equal("lyrics_richsynched", query["namespace"]);
        Assert.Equal("track.richsync", query["optional_calls"]);
        Assert.Equal("mxm", query["subtitle_format"]);
        Assert.Equal("Fixture artist", query["q_artist"]);
        Assert.Equal("Fixture track", query["q_track"]);
        Assert.Equal("Fixture artist", query["q_artists"]);
        Assert.Equal("Fixture album", query["q_album"]);
        Assert.Equal("180", query["q_duration"]);
        Assert.False(query.ContainsKey("track_spotify_id"));
        Assert.Equal("desktop-user-token", query["usertoken"]);
        Assert.Equal("180", query["f_subtitle_length"]);
        Assert.False(query.ContainsKey("f_subtitle_length_max_deviation"));
        Assert.Equal("words", query["richsync_compact_type"]);
        Assert.Equal("track_lyrics_translation_status,track_structure,track_performer_tagging", query["part"]);
        Assert.False(query.ContainsKey("duration"));
        Assert.False(query.ContainsKey("deviation"));
        Assert.False(query.ContainsKey("t"));
        Assert.Equal("en-US,en;q=0.9", handler.AcceptLanguage);
        Assert.Equal("10.1.1", handler.AppVersion);
        Assert.Equal("Musixmatch/2025120901 CFNetwork/3860.300.31 Darwin/25.2.0", handler.UserAgent);
        Assert.Equal("application/json", handler.Accept);
        Assert.Equal("apic-appmobile.musixmatch.com", handler.Host);
        Assert.Equal("x-mxm-token-guid=", handler.Cookie);
        Assert.Equal("apic-appmobile.musixmatch.com", handler.Authority);
        var secret = Assert.Single(secrets.Accounts);
        Assert.Equal(AccountA, secret.AccountId);
        Assert.Equal(SecretA, secret.SecretReferenceId);
    }

    [Fact]
    public async Task RichsyncTiming_PreservesLineDurationAndAbsoluteSegmentOffsets()
    {
        var handler = new FixtureHttpHandler
        {
            Payload = PayloadWithRichsync(RichsyncWithSegmentsBody)
        };
        var outcome = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.True(outcome.IsSuccess, outcome.Error?.SafeMessage);
        var result = outcome.RequireValue();
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.Format);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.Content);
        Assert.NotNull(result.TimedLines);
        var lines = result.TimedLines!;
        Assert.Collection(lines, _ => { }, _ => { });
        Assert.Equal(TimeSpan.FromMilliseconds(1234), lines[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1516), lines[0].Duration);
        Assert.NotNull(lines[0].Segments);
        Assert.Collection(lines[0].Segments!,
            segment =>
            {
                Assert.Equal("Fir", segment.Text);
                Assert.Equal(TimeSpan.FromMilliseconds(1234), segment.Start);
                Assert.Null(segment.Duration);
            },
            segment =>
            {
                Assert.Equal("st ", segment.Text);
                Assert.Equal(TimeSpan.FromMilliseconds(1359), segment.Start);
                Assert.Null(segment.Duration);
            },
            segment =>
            {
                Assert.Equal("word", segment.Text);
                Assert.Equal(TimeSpan.FromMilliseconds(1555), segment.Start);
                Assert.Null(segment.Duration);
            });
        Assert.Equal(TimeSpan.FromMilliseconds(2500), lines[1].Start);
        Assert.Null(lines[1].Duration);
        Assert.Single(lines[1].Segments!);
    }

    [Theory]
    [InlineData("1.2345678", "0.0000001", 12345678L, 12345679L)]
    [InlineData("1.2345678", "0.1250000", 12345678L, 13595678L)]
    [InlineData("86399.9999998", "0.0000001", 863999999998L, 863999999999L)]
    public async Task RichsyncTimingRetainsSubMillisecondSourcePrecision(string start, string offset, long lineTicks, long segmentTicks)
    {
        var raw = "[{\"ts\":" + start + ",\"x\":\"First\",\"l\":[{\"c\":\"First\",\"o\":" + offset + "}]}]";
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = PayloadWithRichsync(raw) }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        var line = Assert.Single(result.RequireValue().TimedLines!);
        Assert.Equal(TimeSpan.FromTicks(lineTicks), line.Start);
        Assert.Equal(TimeSpan.FromTicks(segmentTicks), Assert.Single(line.Segments!).Start);
    }

    [Fact]
    public async Task RichsyncDurationRetainsTheDifferenceBetweenPreciseSourceEndpoints()
    {
        var raw = """[{"ts":1.2345678,"te":2.7500001,"x":"First","l":[{"c":"First","o":0}]}]""";
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = PayloadWithRichsync(raw) }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(TimeSpan.FromTicks(15154323), Assert.Single(result.RequireValue().TimedLines!).Duration);
    }

    [Fact]
    public async Task RichsyncTiming_PlainProjectionOmitsSegments()
    {
        var handler = new FixtureHttpHandler
        {
            Payload = PayloadWithRichsync(RichsyncWithSegmentsBody)
        };
        var result = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.PlainText, result.RequireValue().Format);
        Assert.Equal("First\nSecond", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task RichsyncTiming_WordTimedPreferenceStillProjectsLineLrcWithSegments()
    {
        var handler = new FixtureHttpHandler
        {
            Payload = PayloadWithRichsync(RichsyncWithSegmentsBody)
        };
        var result = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest(preferredFormat: ProviderLyricsFormat.WordTimed));

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.RequireValue().Format);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
        Assert.NotNull(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task RichsyncTiming_AvailabilityProjectionOmitsSegmentsButCacheRetainsThem()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler
        {
            Payload = PayloadWithRichsync(RichsyncWithSegmentsBody)
        };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        var availability = await adapter.FetchLyricsAsync(
            Context(AccountA), LyricsRequest(availabilityOnly: true));
        var content = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.True(availability.IsSuccess, availability.Error?.SafeMessage);
        Assert.Null(availability.RequireValue().Content);
        Assert.Null(availability.RequireValue().TimedLines);
        Assert.True(content.IsSuccess, content.Error?.SafeMessage);
        Assert.NotNull(content.RequireValue().TimedLines);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RichsyncTiming_MissingNestedSegmentsDoesNotInventWordTiming()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var result = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Fact]
    public async Task RichsyncTiming_InvalidOptionalSegmentsDiscardOnlyTimingVector()
    {
        var malformed = """
            [{"ts":1.234,"te":2.750,"x":"First","l":[{"c":"First","o":"bad"}]},
             {"ts":2.5,"x":"Second","l":[{"c":"Second","o":0.0}]}]
            """;
        var handler = new FixtureHttpHandler { Payload = PayloadWithRichsync(malformed) };
        var result = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Theory]
    [InlineData("\"bad\"")]
    [InlineData("0")]
    [InlineData("86401")]
    public async Task InvalidOptionalRichsyncEndKeepsValidLinesWithoutOtherFallbacks(string end)
    {
        var raw = "[{\"ts\":1.234,\"te\":" + end + ",\"x\":\"First\",\"l\":[{\"c\":\"First\",\"o\":0}]}]";
        var payload = JsonNode.Parse(PayloadWithRichsync(raw))!;
        var calls = payload["message"]!["body"]!["macro_calls"]!.AsObject();
        calls.Remove("track.subtitles.get");
        calls.Remove("track.lyrics.get");
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = payload.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("[00:01.234]First", result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedOptionalRichsyncTimingKeepsValidLineLyrics(bool characterBudget)
    {
        var text = characterBudget ? new string('x', 700_000) : "Words";
        var segments = Enumerable.Repeat(new { c = text, o = 0 }, characterBudget ? 1 : 50_001).ToArray();
        var raw = JsonSerializer.Serialize(new[] { new { ts = 1.234, x = text, l = segments } });
        var payload = PayloadWithRichsync(raw, compactEncoding: true);
        Assert.InRange(Encoding.UTF8.GetByteCount(payload), 1, 1_999_999);
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = payload }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("[00:01.234]" + text, result.RequireValue().Content);
        Assert.Null(result.RequireValue().TimedLines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitTokenProbeOmitsTimingForPlainAndAvailability(bool availability)
    {
        using var http = new HttpClient(new FixtureHttpHandler { Payload = PayloadWithRichsync(RichsyncWithSegmentsBody) });
        var result = await MusixmatchLyricsCapabilityAdapter.ReadLyricsFromTokenAsync(http, "fixture-token",
            LyricsRequest(availabilityOnly: availability, preferredFormat: ProviderLyricsFormat.PlainText), default);
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Null(result.RequireValue().TimedLines);
        if (availability) Assert.Null(result.RequireValue().Content);
        else Assert.Equal("First\nSecond", result.RequireValue().Content);
    }

    [Fact]
    public async Task RichsyncTiming_ChangesRevisionWithoutChangingRenderedLines()
    {
        var withTiming = await CreateAdapter(
                new FixtureHttpHandler { Payload = PayloadWithRichsync(RichsyncWithSegmentsBody) },
                new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        var withoutTiming = await CreateAdapter(
                new FixtureHttpHandler { Payload = TimedMacroPayload },
                new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.Equal(withTiming.RequireValue().Content, withoutTiming.RequireValue().Content);
        Assert.NotEqual(withTiming.RequireValue().Revision, withoutTiming.RequireValue().Revision);
        Assert.NotNull(withTiming.RequireValue().TimedLines);
        Assert.Null(withoutTiming.RequireValue().TimedLines);
    }

    [Fact]
    public async Task PlainPreference_ProjectsThePlainMacroWithoutInventingTiming()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var outcome = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(outcome.IsSuccess, outcome.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.PlainText, outcome.RequireValue().Format);
        Assert.Equal("First\nSecond", outcome.RequireValue().Content);
    }

    [Fact]
    public async Task AvailabilityOnly_ProjectsFullCachedResult()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        var content = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));
        var availability = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(availabilityOnly: true, preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(content.IsSuccess, content.Error?.SafeMessage);
        Assert.True(availability.IsSuccess, availability.Error?.SafeMessage);
        Assert.Equal("First\nSecond", content.RequireValue().Content);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, availability.RequireValue().Availability);
        Assert.Null(availability.RequireValue().Content);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AvailabilityThenContent_UsesOneFullResponseFetch()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        var availability = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(availabilityOnly: true, preferredFormat: ProviderLyricsFormat.PlainText));
        var content = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(availability.IsSuccess, availability.Error?.SafeMessage);
        Assert.True(content.IsSuccess, content.Error?.SafeMessage);
        Assert.Null(availability.RequireValue().Content);
        Assert.Equal("First\nSecond", content.RequireValue().Content);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Cache_IsolatedByAccountRevisionSecretMetadataAndRequestedFormat()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 2, secretReferenceId: SecretA),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretB),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountB, revision: 1, secretReferenceId: SecretA),
            LyricsRequest());

        handler.Payload = TimedMacroPayload.Replace(
            "Fixture track",
            "Alternate track",
            StringComparison.Ordinal);
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest(title: "Alternate track"));

        Assert.Equal(6, handler.Requests);
        var keys = cache.GetKeysByPattern("lyrics:v4:*").ToArray();
        Assert.Equal(6, keys.Length);
        Assert.Contains(keys, key => key.Contains(AccountA.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(AccountB.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(SecretA.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(SecretB.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(":2:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, key => key.Contains("desktop-user-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DifferentIsrcDoesNotReuseCachedLyrics()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = DesktopMacroPayload };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());
        foreach (var isrc in new[] { "USABC1234567", "USABC1234568" })
        {
            var result = await adapter.FetchLyricsAsync(Context(), new(RecordingId,
                new("musixmatch", ProviderResourceKind.Track, "lookup-key"), trackTitle: "Fixture track",
                artistNames: ["Fixture artist"], isrc: isrc));
            Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        }
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Cache_DoesNotReuseStaleDesktopEntries(int version)
    {
        EnsureCacheSettings();
        var cache = new TestMemoryApplicationCache();
        var metadata = string.Join(
            "\n",
            [
                MusixmatchLyricsCapabilityAdapter.StableProviderId,
                ProviderResourceKind.Track.ToString(),
                "track-42",
                "Fixture track",
                "Fixture artist",
                "Fixture album",
                "180"
            ]);
        if (version == 3) metadata += "\n";
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(metadata))).ToLowerInvariant();
        var staleKey = $"lyrics:v{version}:{MusixmatchLyricsCapabilityAdapter.StableProviderId}:" +
            $"{AccountA:N}:1:{SecretA:N}:{fingerprint}:linetimed";
        await cache.SetAsync(staleKey,
            new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
                MusixmatchLyricsCapabilityAdapter.StableProviderId,
                ProviderLyricsFormat.LineTimed,
                "[00:99.999]stale-desktop"),
            TimeSpan.FromMinutes(5));

        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var result = await CreateAdapter(handler, new FixtureSecretAccessor(), cache)
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
        Assert.Equal(1, handler.Requests);
        Assert.Single(cache.GetKeysByPattern($"lyrics:v{version}:*"));
        Assert.Single(cache.GetKeysByPattern("lyrics:v4:*"));
    }

    [Fact]
    public async Task SharedAccount_UsesTheSelectedSecretWithoutPublicFallback()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);

        var outcome = await adapter.FetchLyricsAsync(
            Context(AccountA, scope: ProviderAccountScope.Shared),
            LyricsRequest());

        Assert.True(outcome.IsSuccess, outcome.Error?.SafeMessage);
        Assert.Equal(1, handler.Requests);
        var account = Assert.Single(secrets.Accounts);
        Assert.Equal(AccountA, account.AccountId);
        Assert.Null(account.OwnerUserId);
    }

    [Fact]
    public async Task PublicContextIsDeniedBeforeSecretOrHttp()
    {
        var handler = new FixtureHttpHandler();
        var secrets = new FixtureSecretAccessor();
        var result = await CreateAdapter(handler, secrets).FetchLyricsAsync(PublicContext(), LyricsRequest());

        Assert.Equal(ProviderErrorKind.Forbidden, result.Error?.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task MissingSecretReferenceIsConfigurationFailureBeforeHttp()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);

        var result = await adapter.FetchLyricsAsync(
            MissingSecretContext(),
            LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"userToken\":\"\"}")]
    [InlineData("{\"userToken\":")]
    public async Task MissingOrMalformedSecretBundlesAreConfigurationFailures(string bundle)
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = FixtureSecretAccessor.FromBundle(bundle);
        var adapter = CreateAdapter(handler, secrets);

        var result = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.AccountNeedsReauthentication)]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, ProviderErrorKind.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    public async Task UpstreamStatusesBecomeTypedSafeOutcomes(
        HttpStatusCode status,
        ProviderErrorKind expected)
    {
        var handler = new FixtureHttpHandler
        {
            StatusCode = status,
            Payload = "{\"message\":{\"header\":{\"status_code\":500}}}"
        };
        if (status == HttpStatusCode.TooManyRequests)
            handler.RetryAfter = TimeSpan.FromSeconds(7);
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var result = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error!.Kind);
        Assert.DoesNotContain("musixmatch", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        if (expected == ProviderErrorKind.RateLimited)
            Assert.Equal(TimeSpan.FromSeconds(7), result.Error.RetryAfter);
    }

    [Fact]
    public async Task NotFoundAndEmptyMacroAreSafeUnavailableResults()
    {
        var handler = new FixtureHttpHandler
        {
            StatusCode = HttpStatusCode.NotFound,
            Payload = MissingMacroPayload
        };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var notFound = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());
        handler.StatusCode = HttpStatusCode.OK;
        handler.Payload = MissingMacroPayload;
        var empty = await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 2),
            LyricsRequest());

        Assert.True(notFound.IsSuccess, notFound.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, notFound.RequireValue().Availability);
        Assert.True(empty.IsSuccess, empty.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, empty.RequireValue().Availability);
    }

    [Fact]
    public async Task IdentityMismatchIsAnUnavailableMissWithoutProviderDetails()
    {
        var handler = new FixtureHttpHandler
        {
            Payload = TimedMacroPayload.Replace(
                "Fixture track",
                "Unrelated recording",
                StringComparison.Ordinal)
        };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var result = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(
            ProviderLyricsAvailabilityState.Unavailable,
            result.RequireValue().Availability);
    }

    [Fact]
    public async Task MetadataLookupKeyIsNotAnUpstreamMusixmatchTrackId()
    {
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = TimedMacroPayload }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest(trackId: new string('a', 64)));
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
    }

    [Fact]
    public async Task UntimedSubtitleCannotPretendToBeTimedAndFallsBackToPlain()
    {
        var node = JsonNode.Parse(TimedMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        calls.Remove("track.richsync.get");
        calls["track.subtitles.get"]!["message"]!["body"]!["subtitle_list"]![0]!["subtitle"]!["subtitle_body"] = "Synthetic untimed subtitle";
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.Equal(ProviderLyricsFormat.PlainText, result.RequireValue().Format);
        Assert.Equal("First\nSecond", result.RequireValue().Content);
    }

    [Fact]
    public async Task MalformedApiStatusIsACompatibilityFailure()
    {
        var result = await CreateAdapter(new FixtureHttpHandler
        { Payload = "{\"message\":{\"header\":{\"status_code\":\"200\"},\"body\":{}}}" }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.Equal("provider-contract-changed", result.Error?.Code);
    }

    [Fact]
    public async Task RichsyncBeyondOneDayIsAContractFailure()
    {
        var node = JsonNode.Parse(TimedMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        calls.Remove("track.subtitles.get");
        calls.Remove("track.lyrics.get");
        calls["track.richsync.get"]!["message"]!["body"]!["richsync"]!["richsync_body"] = "[{\"ts\":86400.001,\"x\":\"Synthetic overflow\"}]";
        var handler = new FixtureHttpHandler { Payload = node.ToJsonString() };
        var result = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, result.Error!.Kind);
    }

    [Fact]
    public async Task UnavailableResultsAreNotCached()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { StatusCode = HttpStatusCode.NotFound };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        var first = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());
        handler.StatusCode = HttpStatusCode.OK;
        handler.Payload = TimedMacroPayload;
        var second = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, first.RequireValue().Availability);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, second.RequireValue().Availability);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task MalformedOrOversizedResponsesBecomeContractFailures()
    {
        var handler = new FixtureHttpHandler { Payload = "{\"message\":" };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var malformed = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());
        handler.Payload = new string('x', 2_000_001);
        var oversized = await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 2),
            LyricsRequest());

        Assert.False(malformed.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, malformed.Error!.Kind);
        Assert.False(oversized.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, oversized.Error!.Kind);
        Assert.DoesNotContain("message", malformed.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x", oversized.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledExpiredAndForeignRequestsNeverUseHttp()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var canceledResult = await adapter.FetchLyricsAsync(
            Context(AccountA, cancellationToken: canceled.Token),
            LyricsRequest());
        var expiredResult = await adapter.FetchLyricsAsync(
            Context(AccountA, deadline: DateTimeOffset.UtcNow.AddSeconds(-1)),
            LyricsRequest());
        var foreign = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(providerId: "other-provider"));

        Assert.Equal(ProviderErrorKind.Canceled, canceledResult.Error!.Kind);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, expiredResult.Error!.Kind);
        Assert.Equal(ProviderErrorKind.Forbidden, foreign.Error!.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CancellationDuringWarmCacheReadReturnsCanceled()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets, new BlockingApplicationCache());
        using var canceled = new CancellationTokenSource();

        var pending = adapter.FetchLyricsAsync(
            Context(AccountA, cancellationToken: canceled.Token),
            LyricsRequest());
        await Task.Yield();
        canceled.Cancel();
        var result = await pending;

        Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    private static MusixmatchLyricsCapabilityAdapter CreateAdapter(
        FixtureHttpHandler handler,
        FixtureSecretAccessor secrets,
        IApplicationCache? cache = null) =>
        new(
            new HttpClient(handler),
            secrets,
            cache ?? new TestMemoryApplicationCache());

    private static ProviderLyricsRequest LyricsRequest(
        bool availabilityOnly = false,
        ProviderLyricsFormat? preferredFormat = ProviderLyricsFormat.LineTimed,
        string title = "Fixture track",
        string artist = "Fixture artist",
        string providerId = MusixmatchLyricsCapabilityAdapter.StableProviderId,
        string trackId = "track-42") =>
        new(
            RecordingId,
            new ProviderExternalResourceId(
                providerId,
                ProviderResourceKind.Track,
                trackId),
            availabilityOnly,
            preferredFormat,
            title,
            [artist],
            "Fixture album",
            180);

    private static ProviderExecutionContext Context(
        Guid accountId = default,
        long revision = 1,
        Guid secretReferenceId = default,
        ProviderAccountScope scope = ProviderAccountScope.Personal,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var selectedAccount = accountId == Guid.Empty ? AccountA : accountId;
        var selectedSecret = secretReferenceId == Guid.Empty ? SecretA : secretReferenceId;
        return BuildContext(
            selectedAccount,
            revision,
            selectedSecret,
            scope,
            deadline,
            cancellationToken);
    }

    private static ProviderExecutionContext MissingAccountContext() =>
        BuildContext(
            accountId: null,
            revision: 1,
            secretReferenceId: SecretA,
            scope: ProviderAccountScope.Personal,
            deadline: null,
            cancellationToken: default);

    private static ProviderExecutionContext MissingSecretContext() =>
        BuildContext(
            accountId: AccountA,
            revision: 1,
            secretReferenceId: null,
            scope: ProviderAccountScope.Personal,
            deadline: null,
            cancellationToken: default);

    private static ProviderExecutionContext BuildContext(
        Guid? accountId,
        long revision,
        Guid? secretReferenceId,
        ProviderAccountScope scope,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        var account = accountId.HasValue
            ? new ProviderAccountContext(
                accountId.Value,
                MusixmatchLyricsCapabilityAdapter.StableProviderId,
                scope,
                revision,
                ownerUserId: scope == ProviderAccountScope.Personal ? UserId : null,
                secretReferenceId: secretReferenceId)
            : null;
        return new(
            new ProviderActorContext(
                ProviderActorKind.User,
                UserId,
                new ProviderBackendPrincipal("jellyfin", "fixture", "fixture-user")),
            MusixmatchLyricsCapabilityAdapter.StableProviderId,
            account,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(
                    ProviderAudioQuality.Any,
                    ProviderAudioQuality.HighResolution,
                    allowTranscode: true),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: true,
                allowSharedAccount: true,
                allowManagedDownloads: false,
                allowedProviderIds: [MusixmatchLyricsCapabilityAdapter.StableProviderId]),
            "lyrics-read",
            "lyrics-correlation",
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
    }

    private static ProviderExecutionContext PublicContext() => new(
        new ProviderActorContext(ProviderActorKind.PublicRead, null),
        MusixmatchLyricsCapabilityAdapter.StableProviderId,
        account: null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(
                ProviderAudioQuality.Any,
                ProviderAudioQuality.HighResolution,
                allowTranscode: true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: true,
            allowSharedAccount: true,
            allowManagedDownloads: false,
            allowedProviderIds: [MusixmatchLyricsCapabilityAdapter.StableProviderId]),
        "lyrics-public",
        "lyrics-public-correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts.Length == 1 ? string.Empty : parts[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static void EnsureCacheSettings() =>
        CacheExtensions.InitializeCacheSettings(new ServiceCollection().BuildServiceProvider());

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();
        public List<Uri> RequestUris { get; } = [];
        public bool ThrowRequestUriInException { get; set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string Payload { get; set; } = TimedMacroPayload;
        public TimeSpan? RetryAfter { get; set; }
        public int Requests { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Cookie { get; private set; }
        public string? Authority { get; private set; }
        public string? AcceptLanguage { get; private set; }
        public string? AppVersion { get; private set; }
        public string? UserAgent { get; private set; }
        public string? Accept { get; private set; }
        public string? Host { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            AppVersion = request.Headers.TryGetValues("x-mxm-app-version", out var versions) ? versions.Single() : null;
            UserAgent = request.Headers.UserAgent.ToString();
            Accept = request.Headers.Accept.ToString();
            Host = request.Headers.Host;
            Method = request.Method;
            RequestUri = request.RequestUri;
            RequestUris.Add(request.RequestUri!);
            if (ThrowRequestUriInException) throw new HttpRequestException($"Failed request {request.RequestUri}");
            Authority = request.Headers.TryGetValues("authority", out var authorities) ? authorities.Single() : null;
            Cookie = request.Headers.TryGetValues("Cookie", out var values)
                ? values.SingleOrDefault()
                : null;
            AcceptLanguage = request.Headers.NonValidated.TryGetValues("Accept-Language", out var languages)
                ? languages.ToString()
                : null;
            var (status, payload) = Responses.Count > 0 ? Responses.Dequeue() :
                (StatusCode, request.RequestUri!.AbsolutePath.EndsWith("/track.search", StringComparison.Ordinal)
                    ? """{"message":{"header":{"status_code":200},"body":{"track_list":[]}}}""" : Payload);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (RetryAfter is { } retry)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retry);
            return Task.FromResult(response);
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, string Message, LogLevel Level)> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
        public void Dispose() { }
        private sealed class Capture(CapturedLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Messages.Enqueue((category,
                    formatter(state, exception) + exception?.ToString() +
                    (state is IEnumerable<KeyValuePair<string, object?>> fields
                        ? string.Join('|', fields.Select(field => field.Value?.ToString())) : state?.ToString()), level));
        }
    }

    private sealed class FixtureSecretAccessor : IProviderAccountSecretAccessor
    {
        private readonly string _bundle;

        private FixtureSecretAccessor(string bundle, bool rawBundle)
        {
            _ = rawBundle;
            _bundle = bundle;
        }

        public FixtureSecretAccessor(string token = "desktop-user-token")
            : this(JsonSerializer.Serialize(new { userToken = token }), rawBundle: false)
        {
        }

        public static FixtureSecretAccessor FromBundle(string bundle) => new(bundle, rawBundle: true);

        public List<ProviderAccountContext> Accounts { get; } = [];

        public async Task<T> UseAsync<T>(
            ProviderAccountContext account,
            Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            Accounts.Add(account);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetBytes(_bundle);
            try
            {
                return await operation(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private sealed class BlockingApplicationCache : IApplicationCache
    {
        public bool IsEnabled => true;

        public Task<string?> GetStringAsync(string key) => Never<string>();

        public Task<T?> GetAsync<T>(string key) where T : class => Never<T>();

        public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) =>
            Task.FromResult(false);

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(string key) => Task.FromResult(false);

        public Task<bool> ExistsAsync(string key) => Task.FromResult(false);

        public Task<int> DeleteByPatternAsync(string pattern) => Task.FromResult(0);

        public Task<int> PurgeAllAsync() => Task.FromResult(0);

        private static Task<T?> Never<T>() where T : class =>
            new TaskCompletionSource<T?>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }
}
