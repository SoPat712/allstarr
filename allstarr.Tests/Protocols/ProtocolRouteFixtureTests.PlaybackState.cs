using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Playback;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinHomeProgress_DuplicateStartRevalidatesResourceAndRefreshesOnlyHome(bool denied)
    {
        var gateway = PlaybackStateGateway();
        var signals = new Mock<IPlaybackSignalPipeline>();
        var forwarded = new List<(string Path, string Body)>();
        using var factory = PlaybackStateFactory(gateway, forwarded, signals, () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"same","PositionTicks":0,"IsPaused":false}""", "playback-device"));
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        var before = Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        if (denied)
            gateway.Setup(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
                .ThrowsAsync(new UnauthorizedAccessException("Fixture source unavailable"));

        using var duplicate = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"same","PositionTicks":360000000,"IsPaused":true}""", "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
        var observed = Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Equal(denied ? 0L : 360000000L, observed.PositionTicks);
        Assert.Equal(!denied, observed.IsPaused);
        if (denied) Assert.Equal(before.PositionUpdatedAt, observed.PositionUpdatedAt);
        else Assert.NotNull(observed.PositionUpdatedAt);
        gateway.Verify(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.Exactly(2));
        Assert.Single(forwarded, item => item.Path == "/Sessions/Playing");
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.Start), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0L, manager.GetExternalReport(new("primary", "fixture-user", "playback-device"))!["PositionTicks"]!.GetValue<long>());
    }

    [Fact]
    public async Task JellyfinHomeProgress_AcceptedInferredStopRemovesOldHomeWhenNewMetadataIsDenied()
    {
        var gateway = PlaybackStateGateway();
        gateway.Setup(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "43"))
            .ThrowsAsync(new UnauthorizedAccessException("Fixture source unavailable"));
        var signals = new Mock<IPlaybackSignalPipeline>();
        var forwarded = new List<(string Path, string Body)>();
        using var factory = PlaybackStateFactory(gateway, forwarded, signals, () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"first","PositionTicks":120000000,"IsPaused":false}""", "playback-device"));

        using var progress = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-song-43","PlaySessionId":"second","PositionTicks":0,"IsPaused":false}""", "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        Assert.Empty(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Null(manager.GetLastPlayingState(new("primary", "fixture-user", "playback-device")).ItemId);
        Assert.Single(forwarded, item => item.Path == "/Sessions/Playing/Stopped");
        Assert.DoesNotContain(forwarded, item => item.Path == "/Sessions/Playing/Progress");
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.InferredStop), It.IsAny<CancellationToken>()), Times.Once);
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.Start), It.IsAny<CancellationToken>()), Times.Once);
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.InferredStart || signal.Transition == PlaybackTransition.Progress), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinHomeProgress_IsObservedWhenUpstreamRejectsOrThrows(bool throws)
    {
        var reject = true;
        var forwarded = new List<(string Path, string Body)>();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded, signals, () => false,
            playbackResponse: (path, _) => reject && path.EndsWith("/Progress", StringComparison.Ordinal)
                ? throws ? throw new HttpRequestException("Fixture upstream unavailable") : Json(503, "{}") : null);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":0,"IsPaused":false}""", "playback-device"));
        using var progress = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":360000000,"IsPaused":false}""", "playback-device"));

        Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        var state = Assert.Single(factory.Services.GetRequiredService<JellyfinSessionManager>().GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Equal(360000000L, state.PositionTicks);
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.Progress), It.IsAny<CancellationToken>()), Times.Never);
        reject = false;
        using var retry = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":360000000,"IsPaused":false}""", "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(2, forwarded.Count(item => item.Path.EndsWith("/Progress", StringComparison.Ordinal)));
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.Progress), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JellyfinHomeProgress_DoesNotBypassExistingResourceAuthorization()
    {
        var gateway = PlaybackStateGateway();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(gateway, [], signals, () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PositionTicks":0,"IsPaused":false}""", "playback-device"));
        gateway.Setup(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ThrowsAsync(new UnauthorizedAccessException("Fixture source unavailable"));

        using var progress = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-song-42","PositionTicks":360000000,"IsPaused":true}""", "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        var state = Assert.Single(factory.Services.GetRequiredService<JellyfinSessionManager>().GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Equal(0L, state.PositionTicks);
        Assert.False(state.IsPaused);
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.Progress), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JellyfinHomeProgress_HeldStopCannotClearANewerOccurrence()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var forwarded = new List<(string Path, string Body)>();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded, signals, () => false,
            playbackResponse: (path, _) =>
            {
                if (path.EndsWith("/Stopped", StringComparison.Ordinal) && Interlocked.CompareExchange(ref held, 1, 0) == 0)
                {
                    entered.TrySetResult();
                    release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                return null;
            });
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"first","PositionTicks":0,"IsPaused":false}""", "playback-device"));
        var older = client.SendAsync(PlaybackRequest("/Sessions/Playing/Stopped",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"first","PositionTicks":100000000}""", "playback-device"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var next = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
                """{"ItemId":"ext-deezer-song-42","PlaySessionId":"second","PositionTicks":0,"IsPaused":false}""", "playback-device"));
            release.TrySetResult();
            using var completed = await older.WaitAsync(TimeSpan.FromSeconds(5));
            var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
            Assert.Equal(0L, Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5))).PositionTicks);
            Assert.Equal("second", manager.GetExternalReport(new("primary", "fixture-user", "playback-device"))!["PlaySessionId"]!.GetValue<string>());
            signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.Start), It.IsAny<CancellationToken>()), Times.Exactly(2));
            signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.InferredStop || signal.Transition == PlaybackTransition.Stop), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            release.TrySetResult();
            try { (await older.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinHomeProgress_OlderCompletionCannotRegressOrReviveStoppedPlayback(bool stop)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwarded = new List<(string Path, string Body)>();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded, signals, () => false,
            playbackResponse: (path, payload) =>
            {
                if (path.EndsWith("/Progress", StringComparison.Ordinal) && payload.GetProperty("PositionTicks").GetInt64() == 100000000L)
                {
                    entered.TrySetResult();
                    release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                return null;
            });
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":0,"IsPaused":false}""", "playback-device"));
        var older = client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":100000000,"IsPaused":false}""", "playback-device"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var newer = await client.SendAsync(PlaybackRequest(stop ? "/Sessions/Playing/Stopped" : "/Sessions/Playing/Progress",
                """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":400000000,"IsPaused":false}""", "playback-device"));
            release.TrySetResult();
            using var completed = await older.WaitAsync(TimeSpan.FromSeconds(5));
            var states = factory.Services.GetRequiredService<JellyfinSessionManager>().GetActivePlaybackStates(TimeSpan.FromMinutes(5));
            if (stop) Assert.Empty(states);
            else Assert.Equal(400000000L, Assert.Single(states).PositionTicks);
            signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal => signal.Transition == PlaybackTransition.Progress), It.IsAny<CancellationToken>()), Times.Exactly(stop ? 1 : 2));
        }
        finally
        {
            release.TrySetResult();
            try { (await older.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("Feishin")]
    [InlineData("Jellyfin Web")]
    [InlineData("Finamp")]
    public async Task JellyfinExternalReports_PreserveClientStateAndOpenableSessionIdentity(string clientName)
    {
        const string id = "ext-deezer-song-42";
        const string device = "playback-device";
        var forwarded = new List<(string Path, string Body)>();
        var gateway = PlaybackStateGateway();
        var signals = new Mock<IPlaybackSignalPipeline>();
        var stopped = false;
        using var factory = PlaybackStateFactory(gateway, forwarded, signals, () => stopped);
        await PreparePlaybackSessionAsync(factory, "fixture-user", device);
        using var client = factory.CreateClient();
        var queue = """[{"Id":"ext-deezer-song-42","PlaylistItemId":"first"},{"Id":"11111111111111111111111111111111","PlaylistItemId":"native"},{"Id":"ext-deezer-song-42","PlaylistItemId":"repeat","ServerId":"other-server"}]""";
        var initial = clientName == "Feishin"
            ? $$"""{"ItemId":"{{id}}","PositionTicks":0,"NowPlayingQueue":{{queue}}}"""
            : $$"""{"ItemId":"{{id}}","PlaySessionId":"play-1","SessionId":"queue-1","PlaylistItemId":"first","PlaybackStartTimeTicks":638900000000000000,"PositionTicks":0,"CanSeek":false,"IsPaused":false,"IsMuted":true,"PlayMethod":"Transcode","RepeatMode":"RepeatAll","PlaybackOrder":"Shuffle","VolumeLevel":37,"NowPlayingQueue":{{queue}}}""";
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing", initial, device));
        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
        var startBody = PlaybackBody(forwarded[0]);
        Assert.True(Guid.TryParse(startBody.GetProperty("ItemId").GetString(), out _));
        Assert.Equal(3, startBody.GetProperty("NowPlayingQueue").GetArrayLength());
        Assert.True(Guid.TryParse(startBody.GetProperty("Item").GetProperty("AlbumId").GetString(), out _));
        Assert.True(Guid.TryParse(startBody.GetProperty("NowPlayingQueue")[0].GetProperty("Id").GetString(), out _));
        Assert.Equal("11111111111111111111111111111111", startBody.GetProperty("NowPlayingQueue")[1].GetProperty("Id").GetString());
        Assert.Equal("repeat", startBody.GetProperty("NowPlayingQueue")[2].GetProperty("PlaylistItemId").GetString());

        foreach (var (eventName, paused, position) in new[] { ("pause", true, 12000000L), ("seek", true, 950000000L), ("unpause", false, 960000000L) })
        {
            using var progress = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
                $$"""{"ItemId":"{{id}}","EventName":"{{eventName}}","IsPaused":{{paused.ToString().ToLowerInvariant()}},"PositionTicks":{{position}}}""", device));
            Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
            var report = PlaybackBody(forwarded.Last());
            Assert.Equal(paused, report.GetProperty("IsPaused").GetBoolean());
            Assert.Equal(eventName, report.GetProperty("EventName").GetString());
            signals.Verify(service => service.RecordAsync(It.Is<PlaybackSignalRequest>(signal =>
                signal.Transition == PlaybackTransition.Progress && signal.PositionTicks == position && signal.Paused == paused),
                It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal(position, report.GetProperty("PositionTicks").GetInt64());
            Assert.Equal(3, report.GetProperty("NowPlayingQueue").GetArrayLength());
            if (clientName != "Feishin")
            {
                Assert.Equal("play-1", report.GetProperty("PlaySessionId").GetString());
                Assert.Equal("queue-1", report.GetProperty("SessionId").GetString());
                Assert.Equal("first", report.GetProperty("PlaylistItemId").GetString());
                Assert.Equal(638900000000000000L, report.GetProperty("PlaybackStartTimeTicks").GetInt64());
                Assert.True(report.GetProperty("IsMuted").GetBoolean());
                Assert.False(report.GetProperty("CanSeek").GetBoolean());
                Assert.Equal("Transcode", report.GetProperty("PlayMethod").GetString());
                Assert.Equal("RepeatAll", report.GetProperty("RepeatMode").GetString());
                Assert.Equal(37, report.GetProperty("VolumeLevel").GetInt32());
            }
            var providerCalls = gateway.Invocations.Count;
            using var sessions = await client.GetAsync("/Sessions?api_key=fixture-key");
            Assert.Equal(providerCalls, gateway.Invocations.Count);
            Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
            using var document = JsonDocument.Parse(await sessions.Content.ReadAsStringAsync());
            var session = document.RootElement[0];
            Assert.Equal(id, session.GetProperty("NowPlayingItem").GetProperty("Id").GetString());
            Assert.Equal("fixture-server", session.GetProperty("NowPlayingItem").GetProperty("ServerId").GetString());
            Assert.True(session.GetProperty("NowPlayingItem").GetProperty("ImageTags").TryGetProperty("Primary", out _));
            Assert.Equal(paused, session.GetProperty("PlayState").GetProperty("IsPaused").GetBoolean());
            Assert.Equal(position, session.GetProperty("PlayState").GetProperty("PositionTicks").GetInt64());
            Assert.Equal(id, session.GetProperty("NowPlayingQueue")[2].GetProperty("Id").GetString());
            Assert.Equal("repeat", session.GetProperty("NowPlayingQueue")[2].GetProperty("PlaylistItemId").GetString());
            if (clientName != "Feishin")
            {
                Assert.Equal("first", session.GetProperty("PlaylistItemId").GetString());
                Assert.Equal("play-1", session.GetProperty("PlaySessionId").GetString());
            }
        }

        using var sparseProgress = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
            $$"""{"ItemId":"{{id}}","PositionTicks":970000000}""", device));
        var sparseBody = PlaybackBody(forwarded.Last());
        Assert.False(sparseBody.TryGetProperty("EventName", out _));
        Assert.False(sparseBody.GetProperty("IsPaused").GetBoolean());

        using var detail = await client.GetAsync($"/Items/{id}?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var image = await client.GetAsync($"/Items/{id}/Images/Primary");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await image.Content.ReadAsByteArrayAsync());
        using var stop = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Stopped",
            $$"""{"ItemId":"{{id}}","PositionTicks":1700000000,"Failed":false}""", device));
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        var stopBody = PlaybackBody(forwarded.Last());
        Assert.False(stopBody.GetProperty("Failed").GetBoolean());
        Assert.Equal(3, stopBody.GetProperty("NowPlayingQueue").GetArrayLength());
        if (clientName != "Feishin") Assert.Equal("play-1", stopBody.GetProperty("PlaySessionId").GetString());
        stopped = true;
        using var ended = await client.GetAsync("/Sessions?api_key=fixture-key");
        Assert.Equal("[ { \"Id\": \"native-session\", \"DeviceId\": \"playback-device\", \"UserId\": \"fixture-user\", \"PlayState\": {\"IsPaused\":false}, \"Future\": [1,true] } ]",
            await ended.Content.ReadAsStringAsync());
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.AtLeast(6));
        signals.Verify(service => service.RecordAsync(It.Is<PlaybackSignalRequest>(signal =>
            signal.Transition == PlaybackTransition.Stop && signal.ItemId == id &&
            (clientName == "Feishin" || signal.PlaySessionId!.Contains("play-1"))), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JellyfinPlaybackStopAfterSessionRemovalIsInterrupted()
    {
        var forwarded = new List<(string Path, string Body)>();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded, signals, () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"expired","PositionTicks":0}""", "playback-device"));
        await factory.Services.GetRequiredService<JellyfinSessionManager>().RemoveSessionAsync(new("primary", "fixture-user", "playback-device"));
        using var stop = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Stopped",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"expired","PositionTicks":10000000}""", "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        signals.Verify(value => value.RecordAsync(It.Is<PlaybackSignalRequest>(signal =>
            signal.Transition == PlaybackTransition.Stop && signal.Failed), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JellyfinExternalReports_KeepFailureAndPauseFlagsForLearning()
    {
        var forwarded = new List<(string Path, string Body)>();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded, signals, () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"failed","PositionTicks":0}""", "playback-device"));
        using var stop = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Stopped",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"failed","PositionTicks":100000000,"IsPaused":true,"Failed":true}""", "playback-device"));
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        signals.Verify(service => service.RecordAsync(It.Is<PlaybackSignalRequest>(signal =>
            signal.Transition == PlaybackTransition.Stop && signal.Failed && signal.Paused), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JellyfinExternalReports_RepeatedSongOccurrencesHaveSeparateStarts()
    {
        var forwarded = new List<(string Path, string Body)>();
        var gateway = PlaybackStateGateway();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(gateway, forwarded, signals, () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        foreach (var occurrence in new[] { "first", "repeat" })
        {
            using var response = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
                $$"""{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PlaylistItemId":"{{occurrence}}","PositionTicks":0}""", "playback-device"));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Equal(2, forwarded.Count(report => report.Path == "/Sessions/Playing"));
        Assert.Contains(forwarded, report => report.Path == "/Sessions/Playing/Stopped" &&
            PlaybackBody(report).GetProperty("PlaylistItemId").GetString() == "first");
        var starts = signals.Invocations.Select(call => (PlaybackSignalRequest)call.Arguments[0])
            .Where(signal => signal.Transition == PlaybackTransition.Start).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.NotEqual(starts[0].PlaySessionId, starts[1].PlaySessionId);
    }

    [Fact]
    public async Task JellyfinSessions_LeavesOtherOwnersAndNativeRowsByteIdentical()
    {
        const string native = "[ {\"DeviceId\":\"playback-device\",\"UserId\":\"other-user\",\"NowPlayingItem\":{\"Id\":\"native-track\"},\"Future\":1.00} ]";
        var forwarded = new List<(string Path, string Body)>();
        var gateway = PlaybackStateGateway();
        using var factory = PlaybackStateFactory(gateway, forwarded, new Mock<IPlaybackSignalPipeline>(), () => false, native);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PositionTicks":0}""", "playback-device"));
        using var sessions = await client.GetAsync("/Sessions?api_key=fixture-key");
        Assert.Equal(native, await sessions.Content.ReadAsStringAsync());
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinExternalReports_SparseStopRetryKeepsOriginalOccurrenceOnce(bool omitItemId)
    {
        var forwarded = new List<(string Path, string Body)>();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded, signals, () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PlaylistItemId":"first","PositionTicks":0}""", "playback-device"));
        for (var index = 0; index < 2; index++)
        {
            using var stop = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Stopped",
                omitItemId ? """{"PositionTicks":1700000000}""" :
                    """{"ItemId":"ext-deezer-song-42","PositionTicks":1700000000}""", "playback-device"));
        }
        Assert.Single(forwarded, report => report.Path == "/Sessions/Playing/Stopped");
        signals.Verify(service => service.RecordAsync(It.Is<PlaybackSignalRequest>(signal =>
            signal.Transition == PlaybackTransition.Stop && signal.PlaySessionId!.Contains("play-1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JellyfinExternalReports_NewTrackRetainsQueueStateWithoutOldPlayIdentity()
    {
        var gateway = PlaybackStateGateway();
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "43"))
            .ReturnsAsync(PlaybackSong("ext-deezer-song-43", "Next title"));
        var forwarded = new List<(string Path, string Body)>();
        using var factory = PlaybackStateFactory(gateway, forwarded, new Mock<IPlaybackSignalPipeline>(), () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","SessionId":"queue-1","PlaySessionId":"play-1","PlaylistItemId":"first","PositionTicks":100,"IsMuted":true,"VolumeLevel":37,"RepeatMode":"RepeatAll","NowPlayingQueue":[{"Id":"ext-deezer-song-42","PlaylistItemId":"first"},{"Id":"ext-deezer-song-43","PlaylistItemId":"next"}]}""", "playback-device"));
        using var progress = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-song-43","SessionId":"queue-1","PlaySessionId":"play-2","PlaylistItemId":"next","PositionTicks":0}""", "playback-device"));
        var report = PlaybackBody(forwarded.Last());
        Assert.Equal("Next title", report.GetProperty("Item").GetProperty("Name").GetString());
        Assert.Equal("play-2", report.GetProperty("PlaySessionId").GetString());
        Assert.Equal("next", report.GetProperty("PlaylistItemId").GetString());
        Assert.Equal(0, report.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(2, report.GetProperty("NowPlayingQueue").GetArrayLength());
        Assert.True(report.GetProperty("IsMuted").GetBoolean());
        Assert.Equal(37, report.GetProperty("VolumeLevel").GetInt32());
        Assert.Equal("RepeatAll", report.GetProperty("RepeatMode").GetString());
    }

    [Fact]
    public async Task JellyfinExternalReports_MetadataMissRetainsTrustedItemAndArtwork()
    {
        var gateway = PlaybackStateGateway();
        var song = PlaybackSong("ext-deezer-song-42", "Trusted title");
        gateway.SetupSequence(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(song).ReturnsAsync((Song?)null).ReturnsAsync((Song?)null);
        var forwarded = new List<(string Path, string Body)>();
        using var factory = PlaybackStateFactory(gateway, forwarded, new Mock<IPlaybackSignalPipeline>(), () => false);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        foreach (var route in new[] { "", "/Progress", "/Stopped" })
        {
            using var response = await client.SendAsync(PlaybackRequest("/Sessions/Playing" + route,
                """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":10}""", "playback-device"));
        }
        Assert.Equal(3, forwarded.Count);
        Assert.All(forwarded, report => Assert.Equal("Trusted title", PlaybackBody(report).GetProperty("Item").GetProperty("Name").GetString()));
    }

    [Fact]
    public async Task JellyfinExternalReports_LateStopDoesNotClearNextOccurrence()
    {
        var forwarded = new List<(string Path, string Body)>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded,
            new Mock<IPlaybackSignalPipeline>(), () => false);
        var key = await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        foreach (var occurrence in new[] { "first", "repeat" })
        {
            using var start = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
                $$"""{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PlaylistItemId":"{{occurrence}}","PositionTicks":0}""", "playback-device"));
        }
        using var stop = await client.SendAsync(PlaybackRequest("/Sessions/Playing/Stopped",
            """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PlaylistItemId":"first","PositionTicks":1700000000}""", "playback-device"));
        Assert.Equal("ext-deezer-song-42", factory.Services.GetRequiredService<JellyfinSessionManager>().GetLastPlayingItemId(key));
    }

    [Fact]
    public async Task JellyfinExternalReports_RetryAfterUpstreamRejectionIsAcceptedOnce()
    {
        var forwarded = new List<(string Path, string Body)>();
        var signals = new Mock<IPlaybackSignalPipeline>();
        using var factory = PlaybackStateFactory(PlaybackStateGateway(), forwarded, signals, () => false, rejectFirstStart: true);
        await PreparePlaybackSessionAsync(factory, "fixture-user", "playback-device");
        using var client = factory.CreateClient();
        for (var index = 0; index < 3; index++)
        {
            using var response = await client.SendAsync(PlaybackRequest("/Sessions/Playing",
                """{"ItemId":"ext-deezer-song-42","PlaySessionId":"play-1","PositionTicks":0}""", "playback-device"));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Equal(2, forwarded.Count);
        signals.Verify(service => service.RecordAsync(It.Is<PlaybackSignalRequest>(signal =>
            signal.Transition == PlaybackTransition.Start), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IProtocolProviderGateway> PlaybackStateGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var song = PlaybackSong("ext-deezer-song-42", "Playback title");
        song.AlbumId = "ext-deezer-album-7";
        song.CoverArtUrl = "https://artwork.invalid/cover.jpg";
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42")).ReturnsAsync(song);
        gateway.Setup(service => service.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://artwork.invalid/cover.jpg"));
        return gateway;
    }

    private static ProtocolFactory PlaybackStateFactory(Mock<IProtocolProviderGateway> gateway,
        List<(string Path, string Body)> forwarded, Mock<IPlaybackSignalPipeline> signals, Func<bool> stopped,
        string? sessionResponse = null, bool rejectFirstStart = false,
        Func<string, JsonElement, HttpResponseMessage?>? playbackResponse = null) => new("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"fixture-user","Name":"Fixture"}""");
            if (path == "/System/Info/Public") return Json(200, """{"Id":"fixture-server"}""");
            if (request.RequestUri.Host == "artwork.invalid") return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } }
            };
            if (path.StartsWith("/Sessions/Playing", StringComparison.Ordinal))
            {
                forwarded.Add((path, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
                using var payload = JsonDocument.Parse(forwarded.Last().Body);
                if (playbackResponse?.Invoke(path, payload.RootElement) is { } custom) return custom;
                if (rejectFirstStart && path == "/Sessions/Playing" && forwarded.Count == 1) return Json(503, "{}");
                return Json(204, "{}");
            }
            if (path == "/Sessions")
            {
                if (sessionResponse != null) return Json(200, sessionResponse);
                if (stopped()) return Json(200, "[ { \"Id\": \"native-session\", \"DeviceId\": \"playback-device\", \"UserId\": \"fixture-user\", \"PlayState\": {\"IsPaused\":false}, \"Future\": [1,true] } ]");
                var last = JsonNode.Parse(forwarded.Last().Body)!;
                last["Item"]!["Id"] = Guid.Parse(last["Item"]!["Id"]!.GetValue<string>()).ToString("N");
                return Json(200, new JsonArray(new JsonObject
                {
                    ["Id"] = "native-session",
                    ["DeviceId"] = "playback-device",
                    ["UserId"] = "fixture-user",
                    ["NowPlayingItem"] = last["Item"]!.DeepClone(),
                    ["PlayState"] = new JsonObject { ["IsPaused"] = false }
                }).ToJsonString());
            }
            throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
        }, services =>
        {
            AddArtworkPrincipal(services, "fixture-user");
            services.RemoveAll<IApplicationCache>();
            services.AddSingleton<IApplicationCache>(_ => new MemoryApplicationCache(new SystemPlatformClock()));
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IPlaybackSignalPipeline>();
            services.AddSingleton(signals.Object);
        });
}
