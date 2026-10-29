using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("json", false, false)]
    [InlineData("xml", false, false)]
    [InlineData("json", true, false)]
    [InlineData("xml", true, false)]
    [InlineData("json", false, true)]
    [InlineData("xml", false, true)]
    [InlineData("json", true, true)]
    [InlineData("xml", true, true)]
    public async Task SubsonicPlayback_SavedQueueAndNowPlayingFeedUpcomingPreparation(string format, bool report, bool byIndex)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        var clock = new SubsonicPreparationClock();
        var prepared = new TaskCompletionSource<ProtocolExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = new Mock<IUpcomingTrackPreparation>(MockBehavior.Strict);
        preparation.Setup(service => service.PrepareAsync(It.IsAny<ProtocolExecutionContext>(), QueueSongB))
            .Callback<ProtocolExecutionContext, string>((context, _) => prepared.TrySetResult(context))
            .Returns(Task.CompletedTask);
        using var upcoming = new UpcomingSongPreparation(preparation.Object,
            Options.Create(new PlaybackSettings { PrepareUpcomingSongs = true }), clock);
        var gateway = QueueGateway();
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed, configure: services =>
        {
            services.RemoveAll<UpcomingSongPreparation>();
            services.AddSingleton(upcoming);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        string[] ids = byIndex ? [QueueSongA, QueueSongA, QueueSongB] : [QueueSongA, QueueSongB, QueueSongB];
        AssertQueueOk(await SaveQueue(client, byIndex ? "savePlayQueueByIndex" : "savePlayQueue", format, true,
            ids, byIndex ? "currentIndex" : "current", byIndex ? "1" : QueueSongA, "12345", "Feishin"), format);
        upcoming.ProcessPending();
        Assert.Empty(preparation.Invocations);

        var playbackUrl = SubsonicPlaybackUrl(report ? "reportPlayback" : "scrobble", format, "Feishin");
        var parameters = report ? $"&mediaId={QueueSongA}&state=starting&positionMs=0&ignoreScrobble=true"
            : $"&id={QueueSongA}&submission=false";
        AssertQueueOk(await client.GetStringAsync(playbackUrl + parameters), format);
        var signal = Assert.Single(recorded);
        Assert.Equal(PlaybackTransition.Start, signal.Transition);
        Assert.Equal(QueueSongA, signal.ItemId);
        Assert.False(string.IsNullOrWhiteSpace(signal.PlaySessionId));
        if (report) Assert.False(signal.RelayExternally);
        var nowPlaying = await client.GetStringAsync(SubsonicPlaybackUrl("getNowPlaying", format, "Feishin"));
        Assert.Contains(QueueSongA, nowPlaying, StringComparison.Ordinal);
        Assert.Contains("Song 42", nowPlaying, StringComparison.Ordinal);
        Assert.Contains(SubsonicNativeNowPlayingEntry(format), nowPlaying, StringComparison.Ordinal);

        clock.Now = clock.Now.AddSeconds(29);
        upcoming.ProcessPending();
        Assert.Empty(preparation.Invocations);
        clock.Now = clock.Now.AddSeconds(1);
        if (report) AssertQueueOk(await client.GetStringAsync(playbackUrl +
            $"&mediaId={QueueSongA}&state=playing&positionMs=30000&ignoreScrobble=true"), format);
        upcoming.ProcessPending();
        var context = await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(context.IsPreparation);
        Assert.Equal(signal.ExecutionContext.Actor?.EffectiveUserId, context.Actor?.EffectiveUserId);
        var saved = ReadQueue(await client.GetStringAsync(QueueUrl(byIndex ? "getPlayQueueByIndex" : "getPlayQueue",
            format, "Tempo")), format, byIndex ? "playQueueByIndex" : "playQueue");
        Assert.Equal(ids, saved.Ids);
        Assert.Equal(byIndex ? "1" : QueueSongA, saved.Current);
        Assert.Equal(12345, saved.Position);
        Assert.Equal("Feishin", saved.ChangedBy);
        Assert.DoesNotContain(relayed, path => path.Contains("scrobble", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("reportPlayback", StringComparison.OrdinalIgnoreCase) || path.Contains("PlayQueue", StringComparison.OrdinalIgnoreCase));
        preparation.VerifyAll();
    }

    private sealed class SubsonicPreparationClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
