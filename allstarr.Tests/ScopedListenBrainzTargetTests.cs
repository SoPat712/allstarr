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
