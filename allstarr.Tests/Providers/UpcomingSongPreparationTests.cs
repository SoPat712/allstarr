using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class UpcomingSongPreparationTests
{
    [Fact]
    public void UpcomingPreparationIsOffByDefaultWithOneSongWhenEnabled()
    {
        var settings = new PlaybackSettings();
        Assert.False(settings.PrepareUpcomingSongs);
        Assert.Equal(1, settings.PrepareUpcomingCount);
        var probe = new PreparationProbe();
        using var service = Service(probe, settings);
        service.Observe(Context(), PlaybackTransition.Progress, "current", "device", null,
            TimeSpan.FromSeconds(20).Ticks, ["next"]);
        service.ProcessPending();
        Assert.Empty(probe.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientOpenedUpcomingSongSuppressesPreparationAcrossTheHousehold(bool anotherListener)
    {
        using var activity = new allstarr.Services.Common.PlaybackDeliveryActivityStore();
        var context = Context();
        var client = anotherListener
            ? new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "other",
                new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "other", "Listener", false),
                "upcoming-test", context.Deadline, CancellationToken.None, new ProtocolClientDescriptor("client", "other"))
            : Context("other");
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        var lease = new allstarr.Core.Capabilities.ProviderStreamLease("fixture", new Uri("https://media.example.test/audio"),
            context.Deadline, true, true, new("audio/flac", "flac", "flac"),
            allstarr.Core.Capabilities.ProviderStreamRetryBehavior.DoNotRetry);
        activity.StreamOpened(client, "next", allstarr.Core.Capabilities.ProviderAudioQuality.Any,
            new(response, lease, "apple-musickit", "next"));
        response.Dispose();
        var probe = new PreparationProbe();
        using var service = new UpcomingSongPreparation(probe,
            Options.Create(new PlaybackSettings { PrepareUpcomingSongs = true, PrepareUpcomingCount = 2 }), activity: activity);
        service.Observe(context, PlaybackTransition.Progress, "current", "device", null,
            TimeSpan.FromSeconds(20).Ticks, ["next", "last"]);
        service.ProcessPending();
        await probe.WaitForCountAsync(1);
        Assert.Equal(["last"], probe.Items.Keys.ToArray());
        probe.Release.TrySetResult();
    }

    [Fact]
    public void JellyfinQueueUsesPlaylistOccurrenceForRepeatedSongs()
    {
        using var report = JsonDocument.Parse("""
            {"PlaylistItemId":"second","NowPlayingQueue":[
              {"Id":"repeat","PlaylistItemId":"first"},
              {"Id":"other","PlaylistItemId":"middle"},
              {"Id":"repeat","PlaylistItemId":"second"},
              {"Id":"next","PlaylistItemId":"third"},
              {"Id":"last","PlaylistItemId":"fourth"},
              {"Id":"outside","PlaylistItemId":"fifth"}]}
            """);
        Assert.Equal(["next", "last"], UpcomingPlaybackQueue.ParseJellyfin(report.RootElement, "repeat"));
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"NowPlayingQueue\":[]}", 0)]
    [InlineData("{\"NowPlayingQueue\":[{\"Id\":\"different\"}]}", 0)]
    [InlineData("{\"NowPlayingQueue\":[{\"Id\":17}]}", 0)]
    public void MissingQueueCanFallbackButExplicitUnknownQueueCannot(string json, int? count)
    {
        using var report = JsonDocument.Parse(json);
        var next = UpcomingPlaybackQueue.ParseJellyfin(report.RootElement, "current");
        if (count == null) Assert.Null(next);
        else Assert.Equal(count, next!.Count);
    }

    [Fact]
    public async Task PlaylistFallbackWaitsForPlaybackAndPreparesOnlyTwoFollowingItems()
    {
        var context = Context();
        var probe = new PreparationProbe();
        using var service = Service(probe);
        service.RememberPlaylist(context, ["before", "current", "next", "last", "outside"]);
        service.Observe(context, PlaybackTransition.Progress, "current", "device", null, TimeSpan.FromSeconds(9).Ticks);
        service.ProcessPending();
        Assert.Empty(probe.Items);
        service.Observe(context, PlaybackTransition.Progress, "current", "device", null, TimeSpan.FromSeconds(10).Ticks);
        service.ProcessPending();
        await probe.WaitForCountAsync(2);
        Assert.Equal(["last", "next"], probe.Items.Keys.Order().ToArray());
        probe.Release.TrySetResult();
    }

    [Fact]
    public async Task HouseholdHasTwoSlotsAndQueueChangesCancelOldWork()
    {
        var probe = new PreparationProbe();
        using var service = Service(probe);
        var first = Context();
        var second = Context("second");
        service.Observe(first, PlaybackTransition.Progress, "current", "device", "session", TimeSpan.FromSeconds(10).Ticks, ["next", "last"]);
        service.Observe(second, PlaybackTransition.Progress, "current", "second", "session", TimeSpan.FromSeconds(10).Ticks, ["third"]);
        service.ProcessPending();
        await probe.WaitForCountAsync(2);
        Assert.Equal(2, probe.Items.Count);
        service.Observe(first, PlaybackTransition.Progress, "current", "device", "session", TimeSpan.FromSeconds(11).Ticks, ["replacement"]);
        await Task.WhenAll(probe.Items.Values.Select(token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { })))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(probe.Items.Values, token => Assert.True(token.IsCancellationRequested));
        probe.Release.TrySetResult();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledOrPausedPlaybackDoesNotPrepare(bool enabled, bool paused)
    {
        var probe = new PreparationProbe();
        using var service = Service(probe, new() { PrepareUpcomingSongs = enabled });
        service.Observe(Context(), PlaybackTransition.Progress, "current", "device", null,
            TimeSpan.FromSeconds(20).Ticks, ["next"], paused);
        service.ProcessPending();
        Assert.Empty(probe.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrStaleProgressCancelsPreparation(bool stale)
    {
        var clock = new PreparationClock();
        var probe = new PreparationProbe();
        using var service = new UpcomingSongPreparation(probe, Options.Create(new PlaybackSettings { PrepareUpcomingSongs = true }), clock);
        var context = Context();
        service.Observe(context, PlaybackTransition.Progress, "current", "device", null, TimeSpan.FromSeconds(20).Ticks, ["next"]);
        service.ProcessPending();
        await probe.WaitForCountAsync(1);
        if (stale) clock.Now += TimeSpan.FromMinutes(2);
        else service.Observe(context, PlaybackTransition.Stop, "current", "device", null, 0);
        service.ProcessPending();
        Assert.True(probe.Items["next"].IsCancellationRequested);
    }

    [Fact]
    public async Task CountOneAndRepeatedReportsDoNotRepeatPreparation()
    {
        var probe = new PreparationProbe();
        using var service = Service(probe, new() { PrepareUpcomingSongs = true, PrepareUpcomingCount = 1 });
        service.Observe(Context(), PlaybackTransition.Progress, "current", "device", null, TimeSpan.FromSeconds(20).Ticks, ["next", "last"]);
        service.ProcessPending();
        await probe.WaitForCountAsync(1);
        service.ProcessPending();
        Assert.Single(probe.Items);
        probe.Release.TrySetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedSubsonicQueueNeedsAnActivePlaybackObservation(bool byIndex)
    {
        var parameters = new allstarr.Services.Subsonic.SubsonicRequestParameters("POST", null, null,
        [new("id", "before", allstarr.Services.Subsonic.SubsonicParameterSource.Query),
         new("id", "current", allstarr.Services.Subsonic.SubsonicParameterSource.Query),
         new("id", "next", allstarr.Services.Subsonic.SubsonicParameterSource.Query),
         new("current", byIndex ? "1" : "current", allstarr.Services.Subsonic.SubsonicParameterSource.Query)]);
        var parsed = UpcomingPlaybackQueue.ParseSubsonic(parameters, byIndex);
        Assert.Equal("current", parsed.Current);
        Assert.Equal(["next"], parsed.Next);
        var probe = new PreparationProbe();
        using var service = Service(probe);
        service.RememberQueue(Context(), parsed.Current, parsed.Next);
        service.ProcessPending();
        Assert.Empty(probe.Items);
        service.Observe(Context(), PlaybackTransition.Progress, "current", "device", null, TimeSpan.FromSeconds(10).Ticks);
        service.ProcessPending();
        await probe.WaitForCountAsync(1);
        Assert.Contains("next", probe.Items.Keys);
        probe.Release.TrySetResult();
    }

    [Fact]
    public void ExplicitEmptyQueueOverridesPlaylistFallback()
    {
        var probe = new PreparationProbe();
        using var service = Service(probe);
        service.RememberPlaylist(Context(), ["current", "next"]);
        service.Observe(Context(), PlaybackTransition.Progress, "current", "device", null, TimeSpan.FromSeconds(20).Ticks, []);
        service.ProcessPending();
        Assert.Empty(probe.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataSaverCurrentStreamForbidsPreparation(bool withoutDevice)
    {
        using var activity = new allstarr.Services.Common.PlaybackDeliveryActivityStore();
        var context = withoutDevice ? new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "principal",
            new AllstarrPrincipal(Guid.NewGuid(), "subsonic", "backend", "principal", "Listener", false),
            "upcoming-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None, new ProtocolClientDescriptor("client")) : Context();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        var lease = new allstarr.Core.Capabilities.ProviderStreamLease("fixture", new Uri("https://media.example.test/audio"),
            context.Deadline, true, true, new("audio/flac", "flac", "flac"),
            allstarr.Core.Capabilities.ProviderStreamRetryBehavior.DoNotRetry);
        activity.StreamOpened(context, "current", allstarr.Core.Capabilities.ProviderAudioQuality.DataSaver,
            new(response, lease, "apple-musickit", "current"));
        var probe = new PreparationProbe();
        using var service = new UpcomingSongPreparation(probe, Options.Create(new PlaybackSettings { PrepareUpcomingSongs = true }), activity: activity);
        service.Observe(context, PlaybackTransition.Progress, "current", context.Client.DeviceId ?? context.Client.ClientId,
            null, TimeSpan.FromSeconds(20).Ticks, ["next"]);
        service.ProcessPending();
        Assert.Empty(probe.Items);
    }

    [Fact]
    public async Task ReducingTheCountCancelsPreparationsOutsideTheNewLimit()
    {
        var settings = new PlaybackSettings { PrepareUpcomingSongs = true, PrepareUpcomingCount = 2 };
        var probe = new PreparationProbe();
        using var service = Service(probe, settings);
        service.Observe(Context(), PlaybackTransition.Progress, "current", "device", null, TimeSpan.FromSeconds(20).Ticks, ["next", "last"]);
        service.ProcessPending();
        await probe.WaitForCountAsync(2);
        settings.PrepareUpcomingCount = 1;
        service.ProcessPending();
        Assert.True(probe.Items["last"].IsCancellationRequested);
        probe.Release.TrySetResult();
    }

    [Theory]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\"}}", true)]
    [InlineData("{\"subsonic-response\":{\"status\":\"failed\"}}", false)]
    [InlineData("<subsonic-response status=\"ok\"/>", true)]
    [InlineData("<subsonic-response status=\"failed\"/>", false)]
    [InlineData("invalid", false)]
    public void FailedQueueSavesCannotReplaceTheActiveQueue(string body, bool success)
    {
        var response = new allstarr.Services.Subsonic.SubsonicProxyResponse(
            System.Text.Encoding.UTF8.GetBytes(body), "application/json", System.Net.HttpStatusCode.OK,
            new Dictionary<string, string[]>());
        Assert.Equal(success, UpcomingPlaybackQueue.IsSuccessfulSubsonicSave(response));
    }

    [Fact]
    public async Task ClientWithoutDeviceRetainsItsRoutingIdentity()
    {
        var context = new ProtocolExecutionContext(ProtocolKind.Subsonic, "backend", "principal",
            new AllstarrPrincipal(Guid.NewGuid(), "subsonic", "backend", "principal", "Listener", false),
            "upcoming-test", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None, new ProtocolClientDescriptor("client"));
        var probe = new PreparationProbe();
        using var service = Service(probe);
        service.Observe(context, PlaybackTransition.Progress, "current", null, null, TimeSpan.FromSeconds(20).Ticks, ["next"]);
        service.ProcessPending();
        await probe.WaitForCountAsync(1);
        Assert.Null(probe.Contexts["next"].Client.DeviceId);
        Assert.Equal("client", probe.Contexts["next"].Client.ClientId);
        probe.Release.TrySetResult();
    }

    internal static ProtocolExecutionContext Context(string device = "device") => new(ProtocolKind.Jellyfin,
        "backend", "principal", new AllstarrPrincipal(Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "jellyfin", "backend", "principal", "Listener", false), "upcoming-test", DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None, new ProtocolClientDescriptor("client", device));

    private static UpcomingSongPreparation Service(PreparationProbe probe, PlaybackSettings? settings = null) =>
        new(probe, Options.Create(settings ?? new() { PrepareUpcomingSongs = true, PrepareUpcomingCount = 2 }));

    private sealed class PreparationClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PreparationProbe : IUpcomingTrackPreparation
    {
        public ConcurrentDictionary<string, CancellationToken> Items { get; } = new();
        public ConcurrentDictionary<string, ProtocolExecutionContext> Contexts { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PrepareAsync(ProtocolExecutionContext context, string itemId)
        {
            Assert.True(context.IsPreparation);
            Contexts[itemId] = context;
            Items[itemId] = context.CancellationToken;
            await Release.Task.WaitAsync(context.CancellationToken);
        }
        public async Task WaitForCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Items.Count < count) await Task.Delay(10, timeout.Token);
        }
    }
}
