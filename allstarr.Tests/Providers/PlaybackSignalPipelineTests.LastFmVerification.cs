using System.Net;
using System.Text.Json;
using allstarr.Core.Playback;
using allstarr.Core.Intelligence;
using allstarr.Services.Scrobbling;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Fact]
    public async Task LastFmUncertain_AcceptedTimeoutVerifiesWithoutResending()
    {
        await AddScrobbleAccountAsync();
        var http = new VerificationHttp { Found = true };
        var delivery = VerificationDelivery(http);
        await delivery.DeliverAsync(AcceptedScrobblePayload(), default);
        await VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default);
        Assert.Equal(1, http.Sends);
        Assert.Equal(1, http.Lookups);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, (await db.PlaybackDeliveryCheckpoints.SingleAsync()).State);
        Assert.Contains("user=fixture-user", Assert.Single(http.Queries));
        Assert.Contains("from=", http.Queries[0]);
        Assert.Contains("to=", http.Queries[0]);
    }

    [Fact]
    public async Task LastFmUncertain_AbsentSettlesThenResendsOnlyOnceAcrossRestart()
    {
        await AddScrobbleAccountAsync();
        var http = new VerificationHttp();
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default));
        Assert.Equal(1, http.Sends);
        await AgeUncertainAttemptAsync();
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default));
        Assert.Equal(2, http.Sends);
        await AgeUncertainAttemptAsync();
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default));
        Assert.Equal(2, http.Sends);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal("ambiguous", (await db.PlaybackDeliveryCheckpoints.SingleAsync()).ProviderCode);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("malformed")]
    [InlineData("incomplete")]
    [InlineData("nowplaying")]
    [InlineData("wrong-time")]
    [InlineData("wrong-artist")]
    public async Task LastFmUncertain_UnprovenHistoryNeverConfirmsOrBlindlyResends(string mode)
    {
        await AddScrobbleAccountAsync();
        var http = new VerificationHttp { Mode = mode };
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default));
        Assert.Equal(1, http.Sends);
        await using var db = await factory.CreateDbContextAsync();
        Assert.NotEqual(ScopedPlaybackScrobbleOutcome.Delivered, (await db.PlaybackDeliveryCheckpoints.SingleAsync()).State);
    }

    [Fact]
    public async Task LastFmUncertain_FailedLookupCanBeVerifiedWhenWorkerRevisits()
    {
        await AddScrobbleAccountAsync();
        var http = new VerificationHttp { Mode = "failure" };
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default));
        http.Mode = "";
        http.Found = true;
        await VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default);
        Assert.Equal(1, http.Sends);
        Assert.Equal(2, http.Lookups);
    }

    [Fact]
    public async Task LastFmUncertain_StartupQueuesOldReviewForExactOwnerWithoutSending()
    {
        await AddScrobbleAccountAsync();
        var http = new VerificationHttp { Mode = "failure" };
        await Assert.ThrowsAsync<ScopedPlaybackScrobbleDeliveryException>(() => VerificationDelivery(http).DeliverAsync(AcceptedScrobblePayload(), default));
        var handler = new PlaybackSignalJobHandler(new Writer(), VerificationDelivery(http), new Lyrics(), factory,
            playbackTracks, recoveryJobs: jobs);
        await handler.RecoverAsync(default);
        await handler.RecoverAsync(default);
        await using var db = await factory.CreateDbContextAsync();
        var job = Assert.Single(await db.Jobs.ToListAsync());
        Assert.Equal(user, job.OwnerUserId);
        Assert.Equal(1, http.Sends);
        var payload = JsonSerializer.Deserialize<PlaybackSignalPayload>(job.PayloadJson)!;
        Assert.Equal(["lastfm"], payload.DestinationTargets);
        http.Mode = "";
        http.Found = true;
        await VerificationDelivery(http).DeliverAsync(payload, default);
        Assert.Equal(1, http.Sends);
    }

    private ScopedPlaybackScrobbleDelivery VerificationDelivery(VerificationHttp http) => new(factory,
        [new LastFmScopedPlaybackScrobbleTarget(new(http), new ScopedRecommendationAccountAccessor(factory, new VerificationSecrets()))],
        new EfPlaybackDeliveryCheckpointStore(factory), playbackTracks);

    private async Task AgeUncertainAttemptAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PlaybackDeliveryCheckpoints.SingleAsync();
        row.AttemptStartedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        row.RetryAfter = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private sealed class VerificationSecrets : allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(allstarr.Core.Capabilities.ProviderAccountContext account,
            Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken) => operation(
                System.Text.Encoding.UTF8.GetBytes("{\"apiKey\":\"fixture-key\",\"sharedSecret\":\"fixture-secret\",\"sessionKey\":\"fixture-session\",\"username\":\"fixture-user\"}"));
    }

    private sealed class VerificationHttp : HttpMessageHandler
    {
        public bool Found;
        public string Mode = "";
        public int Sends, Lookups;
        public long Timestamp;
        public List<string> Queries { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                Sends++;
                var body = await request.Content!.ReadAsStringAsync(token);
                Timestamp = long.Parse(body.Split('&').Single(x => x.StartsWith("timestamp=", StringComparison.Ordinal)).Split('=')[1]);
                throw new HttpRequestException("Uncertain fixture send");
            }
            Lookups++;
            Queries.Add(request.RequestUri!.Query);
            if (Mode == "failure") return new(HttpStatusCode.ServiceUnavailable);
            if (Mode == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
            object[] tracks = Found || Mode is "nowplaying" or "wrong-time" or "wrong-artist"
                ? [new Dictionary<string, object>
                {
                    ["artist"] = new Dictionary<string, string> { ["#text"] = "Artist" },
                    ["name"] = "Track",
                    ["date"] = new { uts = (Mode == "wrong-time" ? Timestamp + 600 : Timestamp).ToString() },
                    ["@attr"] = new { nowplaying = Mode == "nowplaying" ? "true" : "false" }
                }] : [];
            if (tracks.Length > 0) ((Dictionary<string, object>)tracks[0])["artist"] = new Dictionary<string, string> { ["#text"] = Mode == "wrong-artist" ? "Other artist" : "Artist" };
            var json = JsonSerializer.Serialize(new
            {
                recenttracks = new Dictionary<string, object>
                {
                    ["track"] = tracks,
                    ["@attr"] = new { user = "fixture-user", totalPages = Mode == "incomplete" ? "2" : "1", page = "1" }
                }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
