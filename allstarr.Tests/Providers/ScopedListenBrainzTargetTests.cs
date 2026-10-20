using System.Net;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Services.Scrobbling;

namespace allstarr.Tests;

public sealed class ScopedListenBrainzTargetTests
{
    [Fact]
    public async Task PlayingNow_OmitsCompletedListenTimestamp()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        await target.DeliverAsync(Scope(), PlaybackTransition.Start,
            new ScopedPlaybackTrack("Track", "Artist", "Album", 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Body!);
        var listen = Assert.Single(document.RootElement.GetProperty("payload").EnumerateArray());
        Assert.Equal("playing_now", document.RootElement.GetProperty("listen_type").GetString());
        Assert.False(listen.TryGetProperty("listened_at", out _));
        // ListenBrainz reads a chunked body as empty ("zero-length, empty document"); the length must be sent.
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(handler.Body!), handler.ContentLength);
    }

    [Fact]
    public async Task CompletedListen_IncludesTimestamp()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", "Album", 180_000), null,
            DateTimeOffset.FromUnixTimeSeconds(1700000000), "signal", CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Body!);
        var listen = Assert.Single(document.RootElement.GetProperty("payload").EnumerateArray());
        Assert.Equal("single", document.RootElement.GetProperty("listen_type").GetString());
        Assert.Equal(1700000000, listen.GetProperty("listened_at").GetInt64());
    }

    [Fact]
    public async Task CompletedListen_IncludesOnlyKnownRichMetadata()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());
        var track = new ScopedPlaybackTrack("Track", "Artist", "Album", 180_000,
            "Album artist", "11111111-1111-1111-1111-111111111111", 4, ClientClass: "Finamp");

        await target.DeliverAsync(Scope(), PlaybackTransition.Stop, track, null,
            DateTimeOffset.FromUnixTimeSeconds(1700000000), "signal", CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Body!);
        var metadata = Assert.Single(document.RootElement.GetProperty("payload").EnumerateArray())
            .GetProperty("track_metadata");
        var additional = metadata.GetProperty("additional_info");
        Assert.Equal("Album", metadata.GetProperty("release_name").GetString());
        Assert.Equal(180_000, additional.GetProperty("duration_ms").GetInt64());
        Assert.Equal("11111111-1111-1111-1111-111111111111", additional.GetProperty("recording_mbid").GetString());
        Assert.Equal("4", additional.GetProperty("tracknumber").GetString());
        Assert.Equal("Finamp", additional.GetProperty("media_player").GetString());
        Assert.Equal("Allstarr", additional.GetProperty("submission_client").GetString());
    }

    [Fact]
    public async Task UnknownOptionalMetadata_IsOmitted()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        await target.DeliverAsync(Scope(), PlaybackTransition.Start,
            new ScopedPlaybackTrack("Track", "Artist", null, null), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Body!);
        var metadata = Assert.Single(document.RootElement.GetProperty("payload").EnumerateArray())
            .GetProperty("track_metadata");
        Assert.False(metadata.TryGetProperty("release_name", out _));
        var additional = metadata.GetProperty("additional_info");
        Assert.False(additional.TryGetProperty("duration_ms", out _));
        Assert.False(additional.TryGetProperty("recording_mbid", out _));
        Assert.False(additional.TryGetProperty("tracknumber", out _));
    }

    [Fact]
    public async Task ConfiguredHttpsServiceReceivesTheTokenAndListen()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler),
            new AccountAccessor("""{"token":"koito-token","baseUrl":"https://koito.example/apis/listenbrainz/1"}"""));

        await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.FromUnixTimeSeconds(1700000000), "signal", CancellationToken.None);

        Assert.Equal("https://koito.example/apis/listenbrainz/1/submit-listens", handler.RequestUri);
        Assert.Equal("Token", handler.AuthorizationScheme);
        Assert.Equal("koito-token", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task NonHttpsServiceIsRejectedBeforeTheTokenCanBeSent()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler),
            new AccountAccessor("""{"token":"koito-token","baseUrl":"http://koito.example/apis/listenbrainz/1"}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.DeliverAsync(
            Scope(), PlaybackTransition.Stop, new ScopedPlaybackTrack("Track", "Artist", null, 180_000),
            null, DateTimeOffset.UtcNow, "signal", CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9078/1/")]
    [InlineData("http://192.168.20.10:9078/1/")]
    [InlineData("http://10.1.2.3:4110/apis/listenbrainz/1/")]
    [InlineData("http://172.16.0.2:9078/1/")]
    [InlineData("http://[fd00::1]:4110/apis/listenbrainz/1/")]
    [InlineData("http://multi-scrobbler:9078/1/")]
    public async Task PrivateHttpService_ReceivesTheConfiguredSubmission(string address)
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler),
            new AccountAccessor(JsonSerializer.Serialize(new { token = "fixture-token", baseUrl = address })));
        await target.DeliverAsync(Scope(), PlaybackTransition.Submission,
            new ScopedPlaybackTrack("Track", "Artist", null, 180000), null, DateTimeOffset.UtcNow, "signal", default);
        Assert.Equal(address + "submit-listens", handler.RequestUri);
        Assert.Equal("fixture-token", handler.AuthorizationParameter);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.31.0.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void HttpConnection_RequiresEveryResolvedAddressToBePrivate(string address, bool expected)
    {
        Assert.Equal(expected, allstarr.Services.Common.ListenBrainzServiceEndpoint.AllowsResolvedAddresses(
            new("http://scrobbler.internal/1/"), [IPAddress.Parse(address)]));
        Assert.False(allstarr.Services.Common.ListenBrainzServiceEndpoint.AllowsResolvedAddresses(
            new("http://scrobbler.internal/1/"), [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("8.8.8.8")]));
    }

    [Theory]
    [InlineData("multi-scrobbler")]
    [InlineData("koito")]
    public async Task FirstClassTargets_UseTheSharedSenderWithDistinctIdentities(string providerId)
    {
        var handler = new CaptureHandler();
        var accounts = new AccountAccessor("{\"token\":\"fixture-token\",\"baseUrl\":\"http://127.0.0.1:9078/1/\"}");
        using var http = new HttpClient(handler);
        IExactScopePlaybackScrobbleTarget target = providerId == "koito"
            ? new KoitoPlaybackScrobbleTarget(http, accounts) : new MultiScrobblerPlaybackScrobbleTarget(http, accounts);
        Assert.Equal(providerId, target.ProviderId);
        await target.DeliverAsync(Scope(), PlaybackTransition.Submission, new("Track", "Artist", null, 180000),
            null, DateTimeOffset.FromUnixTimeSeconds(1700000000), "signal", default);
        Assert.Equal("http://127.0.0.1:9078/1/submit-listens", handler.RequestUri);
        Assert.Equal("fixture-token", handler.AuthorizationParameter);
        Assert.Contains("1700000000", handler.Body);
    }

    [Fact]
    public async Task PrivateHttpTransport_SendsTokenToTheCheckedSocketAndDoesNotFollowRedirects()
    {
        using var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        var root = $"http://localhost:{port}/";
        listener.Prefixes.Add(root);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var count = 0;
        string? authorization = null;
        var serve = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                var request = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                count++;
                authorization = request.Request.Headers["Authorization"];
                request.Response.StatusCode = count == 1 ? 302 : 200;
                if (count == 1) request.Response.RedirectLocation = root + "unexpected/";
                request.Response.Close();
            }
        });
        try
        {
            using var http = new HttpClient(allstarr.Services.Common.ListenBrainzServiceEndpoint.CreateHttpHandler());
            var target = new ListenBrainzScopedPlaybackScrobbleTarget(http,
                new AccountAccessor(JsonSerializer.Serialize(new { token = "fixture-token", baseUrl = root + "1/" })));
            var result = await target.DeliverAsync(Scope(), PlaybackTransition.Submission,
                new("Track", "Artist", null, 180000), null, DateTimeOffset.UtcNow, "signal", cancellation.Token);
            Assert.Equal(1, count);
            Assert.Equal("Token fixture-token", authorization);
            Assert.Equal(ScopedPlaybackScrobbleOutcome.PermanentFailure, result.Outcome);
        }
        finally
        {
            cancellation.Cancel(); listener.Stop();
            try { await serve; } catch (OperationCanceledException) { } catch (HttpListenerException) { }
        }
    }

    private static IntelligenceScope Scope() => new(Guid.NewGuid(), "jellyfin", "backend");

    private sealed class AccountAccessor(string json = "{\"token\":\"test-token\"}")
        : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(json);
            return await operation(document.RootElement, cancellationToken);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public int Calls { get; private set; }

        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            ContentLength = request.Content!.Headers.ContentLength;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri?.AbsoluteUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

public sealed class ScopedLastFmTargetTests
{
    [Fact]
    public async Task CompletedListen_SendsRichMetadataAndOriginalTimestamp()
    {
        var handler = new CaptureHandler();
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());
        var track = new ScopedPlaybackTrack("Track", "Artist", "Album", 180_000,
            "Album artist", "11111111-1111-1111-1111-111111111111", 4, ChosenByUser: false);

        await target.DeliverAsync(Scope(), PlaybackTransition.Stop, track, null,
            DateTimeOffset.FromUnixTimeSeconds(123456), "signal", CancellationToken.None);

        var form = ParseForm(handler.Body!);
        Assert.Equal("track.scrobble", form["method"]);
        Assert.Equal("123456", form["timestamp"]);
        Assert.Equal("Album", form["album"]);
        Assert.Equal("Album artist", form["albumArtist"]);
        Assert.Equal("180", form["duration"]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", form["mbid"]);
        Assert.Equal("4", form["trackNumber"]);
        Assert.Equal("0", form["chosenByUser"]);
        Assert.Equal(32, form["api_sig"].Length);
    }

    [Fact]
    public async Task NowPlaying_OmitsUnknownAndCompletionOnlyFields()
    {
        var handler = new CaptureHandler();
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        await target.DeliverAsync(Scope(), PlaybackTransition.Start,
            new ScopedPlaybackTrack("Track", "Artist", null, null), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        var form = ParseForm(handler.Body!);
        Assert.Equal("track.updateNowPlaying", form["method"]);
        Assert.DoesNotContain("timestamp", form.Keys);
        Assert.DoesNotContain("chosenByUser", form.Keys);
        Assert.DoesNotContain("album", form.Keys);
        Assert.DoesNotContain("duration", form.Keys);
        Assert.DoesNotContain("mbid", form.Keys);
    }

    [Fact]
    public async Task JsonIgnoredResponse_IsVisibleAndTerminal()
    {
        var handler = new CaptureHandler(responseBody: """
            {"scrobbles":{"scrobble":{"ignoredMessage":{"#text":"Timestamp is too old","code":"1"}},"@attr":{"accepted":"0","ignored":"1"}}}
            """);
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        var result = await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, result.Outcome);
        Assert.Equal("1", result.ProviderCode);
        Assert.Equal("Timestamp is too old", result.SafeMessage);
        using var details = JsonDocument.Parse(result.DetailsJson);
        Assert.Equal(0, details.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(1, details.RootElement.GetProperty("ignored").GetInt32());
    }

    [Fact]
    public async Task XmlAcceptedResponse_PreservesCorrectedValues()
    {
        var handler = new CaptureHandler(responseBody: """
            <lfm status="ok"><scrobbles accepted="1" ignored="0"><scrobble><artist corrected="0">Artist</artist><track corrected="1">Corrected track</track><ignoredMessage code="0"></ignoredMessage></scrobble></scrobbles></lfm>
            """);
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        var result = await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, result.Outcome);
        using var details = JsonDocument.Parse(result.DetailsJson);
        var correction = details.RootElement.GetProperty("corrections").GetProperty("track");
        Assert.True(correction.GetProperty("corrected").GetBoolean());
        Assert.Equal("Corrected track", correction.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(9, ScopedPlaybackScrobbleOutcome.PermanentFailure, true)]
    [InlineData(6, ScopedPlaybackScrobbleOutcome.PermanentFailure, false)]
    [InlineData(16, ScopedPlaybackScrobbleOutcome.Retrying, false)]
    [InlineData(29, ScopedPlaybackScrobbleOutcome.Retrying, false)]
    public async Task ApiErrors_AreClassified(int code, ScopedPlaybackScrobbleOutcome expected, bool reauthenticate)
    {
        var handler = new CaptureHandler(responseBody: JsonSerializer.Serialize(new { error = code, message = "Provider detail" }));
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        var result = await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(reauthenticate, result.RequiresReauthentication);
        Assert.Equal(code.ToString(), result.ProviderCode);
        if (code == 29) Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public async Task RateLimit_HonorsRetryAfter()
    {
        var handler = new CaptureHandler(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(42));
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        var result = await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);
    }

    [Fact]
    public async Task RequestTimeout_IsRetryable()
    {
        var handler = new CaptureHandler(HttpStatusCode.RequestTimeout, "");
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        var result = await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, result.Outcome);
        Assert.Equal("http-408", result.ProviderCode);
    }

    [Fact]
    public async Task MalformedServicePayload_IsRetryableWithoutLeakingBody()
    {
        var handler = new CaptureHandler(responseBody: "[\"token=do-not-copy\"]");
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        var result = await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, result.Outcome);
        Assert.DoesNotContain("do-not-copy", result.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerFailure_CannotBeMaskedByAcceptedBody()
    {
        var handler = new CaptureHandler(HttpStatusCode.ServiceUnavailable,
            "{\"scrobbles\":{\"scrobble\":{},\"@attr\":{\"accepted\":\"1\",\"ignored\":\"0\"}}}");
        var target = new LastFmScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        var result = await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", null, 180_000), null,
            DateTimeOffset.UtcNow, "signal", CancellationToken.None);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Retrying, result.Outcome);
        using var details = JsonDocument.Parse(result.DetailsJson);
        Assert.Equal(503, details.RootElement.GetProperty("httpStatus").GetInt32());
    }

    private static IntelligenceScope Scope() => new(Guid.NewGuid(), "jellyfin", "backend");

    private static Dictionary<string, string> ParseForm(string body) => body.Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
            part => Uri.UnescapeDataString(part[1].Replace('+', ' ')));

    private sealed class AccountAccessor : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse("{\"apiKey\":\"key\",\"sharedSecret\":\"secret\",\"sessionKey\":\"session\"}");
            return await operation(document.RootElement, cancellationToken);
        }
    }

    private sealed class CaptureHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseBody = null,
        TimeSpan? retryAfter = null) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = responseBody ?? (Body.Contains("track.scrobble", StringComparison.Ordinal)
                ? "{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"#text\":\"\",\"code\":\"0\"}},\"@attr\":{\"accepted\":\"1\",\"ignored\":\"0\"}}}"
                : "{\"nowplaying\":{\"track\":{\"#text\":\"Track\",\"corrected\":\"0\"}}}");
            var response = new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
            if (retryAfter.HasValue)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
            return response;
        }
    }
}
