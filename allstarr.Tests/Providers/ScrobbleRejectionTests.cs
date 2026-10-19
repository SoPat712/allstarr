using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Services.Scrobbling;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class ScrobbleRejectionTests
{
    [Theory]
    [InlineData(false, 400)]
    [InlineData(false, 429)]
    [InlineData(false, 503)]
    [InlineData(false, 401)]
    [InlineData(true, 400)]
    [InlineData(true, 429)]
    [InlineData(true, 503)]
    [InlineData(true, 200)]
    public async Task Rejection_PreservesSafeReasonAndLogsOnce(bool lastFm, int status)
    {
        const string reason = "Bad timestamp\n token=hidden https://user:password@example.com/path?api_key=hidden echoed fixture-token";
        var body = lastFm
            ? JsonSerializer.Serialize(new { error = 6, message = reason })
            : JsonSerializer.Serialize(new { code = status, error = reason });
        var handler = new ResponseHandler((HttpStatusCode)status, body);
        var lastLogger = new RecordingLogger<LastFmScopedPlaybackScrobbleTarget>();
        var listenLogger = new RecordingLogger<ListenBrainzScopedPlaybackScrobbleTarget>();
        IExactScopePlaybackScrobbleTarget target = lastFm
            ? new LastFmScopedPlaybackScrobbleTarget(new(handler), new Accounts(), lastLogger)
            : new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts(), logger: listenLogger);

        var result = await Deliver(target);

        Assert.Contains("Bad timestamp", result.SafeMessage);
        using var details = JsonDocument.Parse(result.DetailsJson);
        var safeReason = details.RootElement.GetProperty("providerMessage").GetString()!;
        Assert.InRange(safeReason.Length, 1, 200);
        var warning = Assert.Single(lastFm ? lastLogger.Warnings : listenLogger.Warnings);
        Assert.Contains(lastFm ? "lastfm" : "listenbrainz", warning);
        Assert.Contains(status.ToString(), warning);
        Assert.Contains("single", warning);
        foreach (var text in new[] { result.SafeMessage!, safeReason, warning })
        {
            Assert.DoesNotContain("hidden", text);
            Assert.DoesNotContain("password", text);
            Assert.DoesNotContain("fixture-token", text);
            Assert.DoesNotContain('\n', text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainTextRejection_IsBoundedAndSanitized(bool lastFm)
    {
        var handler = new ResponseHandler(HttpStatusCode.BadRequest, "token=hidden " + new string('x', 10_000));
        IExactScopePlaybackScrobbleTarget target = lastFm
            ? new LastFmScopedPlaybackScrobbleTarget(new(handler), new Accounts())
            : new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts());

        var result = await Deliver(target);

        Assert.Equal(ScopedPlaybackScrobbleOutcome.PermanentFailure, result.Outcome);
        using var details = JsonDocument.Parse(result.DetailsJson);
        var reason = details.RootElement.GetProperty("providerMessage").GetString()!;
        Assert.Equal(200, reason.Length);
        Assert.DoesNotContain("hidden", reason);
        Assert.Contains(reason, result.SafeMessage);
        Assert.InRange(handler.ResponseStream!.BytesRead, 1, 2048);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("{\"error\":42}")]
    [InlineData("null")]
    public async Task UnreadableRejection_RemainsTerminal(string body)
    {
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(
            new(new ResponseHandler(HttpStatusCode.BadRequest, body)), new Accounts());
        var result = await Deliver(target);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("400", result.ProviderCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenErrorBody_PreservesHttpClassification(bool lastFm)
    {
        IExactScopePlaybackScrobbleTarget target = lastFm
            ? new LastFmScopedPlaybackScrobbleTarget(new(new BrokenBodyHandler()), new Accounts())
            : new ListenBrainzScopedPlaybackScrobbleTarget(new(new BrokenBodyHandler()), new Accounts());
        var result = await Deliver(target);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("400", result.ProviderCode);
    }

    [Fact]
    public async Task XmlRejection_ExposesOnlyTheSafeError()
    {
        var handler = new ResponseHandler(HttpStatusCode.OK,
            "<lfm status=\"failed\"><error code=\"6\">Invalid artist token=hidden</error></lfm>");
        var target = new LastFmScopedPlaybackScrobbleTarget(new(handler), new Accounts());
        var result = await Deliver(target);
        Assert.Contains("Invalid artist", result.SafeMessage);
        Assert.DoesNotContain("hidden", result.SafeMessage);
        Assert.DoesNotContain("hidden", result.DetailsJson);
    }

    [Fact]
    public async Task PlayingNowRejection_LogsTheTransitionAndTruncatesJsonReason()
    {
        var handler = new ResponseHandler(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = new string('x', 500) }));
        var logger = new RecordingLogger<ListenBrainzScopedPlaybackScrobbleTarget>();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts(), logger: logger);
        var result = await Deliver(target, transition: PlaybackTransition.Start);
        using var details = JsonDocument.Parse(result.DetailsJson);
        Assert.Equal(200, details.RootElement.GetProperty("providerMessage").GetString()!.Length);
        Assert.Contains("playing_now", Assert.Single(logger.Warnings));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1033430399)]
    [InlineData(4102444800)]
    [InlineData(-62135596800)]
    public async Task InvalidListenTime_IsIgnoredBeforeSending(long unixTime)
    {
        var handler = new ResponseHandler(HttpStatusCode.OK, "");
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts());

        var result = await Deliver(target, occurredAt: DateTimeOffset.FromUnixTimeSeconds(unixTime));

        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, result.Outcome);
        Assert.Equal("invalid-listened-at", result.ProviderCode);
        Assert.Contains("timestamp", result.SafeMessage);
        Assert.Null(handler.RequestBody);
    }

    [Theory]
    [InlineData(1033430400)]
    [InlineData(1700000000)]
    public async Task ValidListenTime_IsPreserved(long unixTime)
    {
        var handler = new ResponseHandler(HttpStatusCode.OK, "");
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts());
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered,
            (await Deliver(target, occurredAt: DateTimeOffset.FromUnixTimeSeconds(unixTime))).Outcome);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(unixTime, body.RootElement.GetProperty("payload")[0].GetProperty("listened_at").GetInt64());
    }

    [Theory]
    [InlineData("", "Artist", null, "empty-metadata")]
    [InlineData("Track", " \t", null, "empty-metadata")]
    [InlineData("Tr\0ack", "Artist", null, "invalid-metadata")]
    [InlineData("Track", "Art\0ist", null, "invalid-metadata")]
    [InlineData("Track", "Artist", "Alb\0um", "invalid-metadata")]
    public async Task InvalidMetadata_IsIgnoredBeforeSending(string title, string artist, string? album, string code)
    {
        var handler = new ResponseHandler(HttpStatusCode.OK, "");
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts());
        var result = await Deliver(target, new(title, artist, album, 180000));
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, result.Outcome);
        Assert.Equal(code, result.ProviderCode);
        Assert.Null(handler.RequestBody);
    }

    [Fact]
    public async Task OversizedListen_IsIgnoredBeforeSending()
    {
        var handler = new ResponseHandler(HttpStatusCode.OK, "");
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts());
        var result = await Deliver(target, new(new string('曲', 4000), "Artist", null, 180000));
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Ignored, result.Outcome);
        Assert.Equal("listen-too-large", result.ProviderCode);
        Assert.Null(handler.RequestBody);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2073600001)]
    [InlineData(long.MaxValue)]
    public async Task InvalidOptionalDuration_IsOmitted(long duration)
    {
        var handler = new ResponseHandler(HttpStatusCode.OK, "");
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts());
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered,
            (await Deliver(target, new("Track", "Artist", null, duration, RecordingMusicBrainzId: "invalid"))).Outcome);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var additional = body.RootElement.GetProperty("payload")[0].GetProperty("track_metadata").GetProperty("additional_info");
        Assert.False(additional.TryGetProperty("duration_ms", out _));
        Assert.False(additional.TryGetProperty("recording_mbid", out _));
    }

    [Fact]
    public async Task PlayingNow_DoesNotValidateUnusedTimestamp()
    {
        var handler = new ResponseHandler(HttpStatusCode.OK, "");
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new(handler), new Accounts());
        var result = await Deliver(target, transition: PlaybackTransition.Start, occurredAt: default(DateTimeOffset));
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, result.Outcome);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(body.RootElement.GetProperty("payload")[0].TryGetProperty("listened_at", out _));
    }

    private static Task<ScopedPlaybackScrobbleResult> Deliver(IExactScopePlaybackScrobbleTarget target,
        ScopedPlaybackTrack? track = null, PlaybackTransition transition = PlaybackTransition.Stop,
        DateTimeOffset? occurredAt = null) => target.DeliverAsync(new(Guid.NewGuid(), "jellyfin", "backend"),
        transition, track ?? new("Track", "Artist", "Album", 180000), null,
        occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-3), "signal", default);

    private sealed class Accounts : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken) => Task.FromResult(true);
        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            using var secret = JsonDocument.Parse("""{"token":"fixture-token","apiKey":"fixture-key","sharedSecret":"fixture-secret","sessionKey":"fixture-session"}""");
            return await operation(secret.RootElement, cancellationToken);
        }
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public CountingStream? ResponseStream { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            ResponseStream = new CountingStream(Encoding.UTF8.GetBytes(body));
            return new(status) { Content = new StreamContent(ResponseStream) };
        }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }

    private sealed class BrokenBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(new BrokenStream()) });
    }

    private sealed class BrokenStream() : MemoryStream([])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Unreadable response."));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
