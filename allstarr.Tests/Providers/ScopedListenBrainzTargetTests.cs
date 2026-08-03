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
    }

    [Fact]
    public async Task CompletedListen_IncludesTimestamp()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());

        await target.DeliverAsync(Scope(), PlaybackTransition.Stop,
            new ScopedPlaybackTrack("Track", "Artist", "Album", 180_000), null,
            DateTimeOffset.FromUnixTimeSeconds(123456), "signal", CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Body!);
        var listen = Assert.Single(document.RootElement.GetProperty("payload").EnumerateArray());
        Assert.Equal("single", document.RootElement.GetProperty("listen_type").GetString());
        Assert.Equal(123456, listen.GetProperty("listened_at").GetInt64());
    }

    [Fact]
    public async Task CompletedListen_IncludesOnlyKnownRichMetadata()
    {
        var handler = new CaptureHandler();
        var target = new ListenBrainzScopedPlaybackScrobbleTarget(new HttpClient(handler), new AccountAccessor());
        var track = new ScopedPlaybackTrack("Track", "Artist", "Album", 180_000,
            "Album artist", "11111111-1111-1111-1111-111111111111", 4, ClientClass: "Finamp");

        await target.DeliverAsync(Scope(), PlaybackTransition.Stop, track, null,
            DateTimeOffset.FromUnixTimeSeconds(123456), "signal", CancellationToken.None);

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

    private static IntelligenceScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), "jellyfin", "backend", "library");

    private sealed class AccountAccessor : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse("{\"token\":\"test-token\"}");
            return await operation(document.RootElement, cancellationToken);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
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

    private static IntelligenceScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), "jellyfin", "backend", "library");

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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
