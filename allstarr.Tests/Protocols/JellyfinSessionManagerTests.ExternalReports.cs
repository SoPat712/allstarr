using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public partial class JellyfinSessionManagerTests
{
    [Fact]
    public async Task HomeObservation_SameReceiptCannotStopADifferentObservedOccurrence()
    {
        var settings = CreateSettings();
        using var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var manager = new JellyfinSessionManager(CreateProxyService(handler, settings), Options.Create(settings), NullLogger<JellyfinSessionManager>.Instance);
        var key = Key("home-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Client", "Phone", "1", CreateHeaders()));
        var receipt = manager.CapturePlaybackReport();
        const string item = "ext-deezer-song-42";
        var current = new JsonObject { ["ItemId"] = item, ["PlaySessionId"] = "second", ["PositionTicks"] = 400000000L };
        Assert.True(manager.ObservePlaybackReport(key, receipt, item, 400000000L, false, current));
        var oldStop = new JsonObject { ["ItemId"] = item, ["PlaySessionId"] = "first", ["PositionTicks"] = 0L };

        Assert.False(manager.UpdateExternalReport(key, oldStop, receipt, stopped: true));
        Assert.Equal(400000000L, Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5))).PositionTicks);
        Assert.True(manager.UpdateExternalReport(key, current, receipt));
        Assert.Equal("second", manager.GetExternalReport(key)!["PlaySessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task HomeObservation_PreservesUnknownFieldsAndOnlyUsesReportedClockAnchors()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
        var settings = CreateSettings();
        using var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var manager = new JellyfinSessionManager(CreateProxyService(handler, settings), Options.Create(settings),
            NullLogger<JellyfinSessionManager>.Instance, timeProvider: clock);
        var key = Key("home-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Client", "Phone", "1", CreateHeaders()));
        const string item = "ext-deezer-song-42";
        var occurrence = new JsonObject { ["PlaySessionId"] = "first" };
        Assert.True(manager.ObservePlaybackReport(key, manager.CapturePlaybackReport(), item, 5500000L, false, occurrence));
        var first = Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Equal(clock.GetUtcNow(), first.PositionUpdatedAt);
        clock.Advance(TimeSpan.FromSeconds(2));
        manager.UpdateActivity(key);
        Assert.True(manager.ObservePlaybackReport(key, manager.CapturePlaybackReport(), item, null, null, occurrence));
        var missing = Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Equal(5500000L, missing.PositionTicks);
        Assert.False(missing.IsPaused);
        Assert.Equal(first.PositionUpdatedAt, missing.PositionUpdatedAt);
        Assert.True(manager.ObservePlaybackReport(key, manager.CapturePlaybackReport(), item, null, true, occurrence));
        var paused = Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.True(paused.IsPaused);
        Assert.Equal(5500000L, paused.PositionTicks);
        Assert.Null(paused.PositionUpdatedAt);
        Assert.True(manager.ObservePlaybackReport(key, manager.CapturePlaybackReport(), item, 9000000L, true, occurrence));
        Assert.Equal(clock.GetUtcNow(), Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5))).PositionUpdatedAt);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(manager.ObservePlaybackReport(key, manager.CapturePlaybackReport(), item, null, false, occurrence));
        Assert.Null(Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5))).PositionUpdatedAt);
        Assert.True(manager.ObservePlaybackReport(key, manager.CapturePlaybackReport(), item, 1000000L, false, occurrence));
        Assert.Equal(1000000L, Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5))).PositionTicks);
        Assert.True(manager.ObservePlaybackReport(key, manager.CapturePlaybackReport(), item, null, null,
            new JsonObject { ["PlaySessionId"] = "repeat" }));
        var repeat = Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Null(repeat.PositionTicks);
        Assert.Null(repeat.IsPaused);
        Assert.Null(repeat.PositionUpdatedAt);
    }

    [Fact]
    public async Task HomeObservation_ReceiptFencesAcknowledgementsAndPairedStopClears()
    {
        var settings = CreateSettings();
        using var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var manager = new JellyfinSessionManager(CreateProxyService(handler, settings), Options.Create(settings), NullLogger<JellyfinSessionManager>.Instance);
        var key = Key("home-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Client", "Phone", "1", CreateHeaders()));
        const string item = "ext-deezer-song-42";
        var report = new JsonObject { ["ItemId"] = item, ["PlaySessionId"] = "current", ["PositionTicks"] = 400000000L };
        var older = manager.CapturePlaybackReport();
        var current = manager.CapturePlaybackReport();
        Assert.True(manager.ObservePlaybackReport(key, current, item, 400000000L, false, report));
        Assert.True(manager.UpdateExternalReport(key, report, current));
        Assert.False(manager.ObservePlaybackReport(key, older, item, 100000000L, true, report));
        Assert.False(manager.UpdateExternalReport(key, report, older, stopped: true));
        manager.MarkExplicitStop(key, item, older);
        manager.UpdatePlayingItem(key, null, null, older);
        Assert.Equal(400000000L, Assert.Single(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5))).PositionTicks);
        Assert.False(manager.WasRecentlyExplicitlyStopped(key, item, TimeSpan.FromMinutes(1)));
        var stop = manager.CapturePlaybackReport();
        Assert.True(manager.ObservePlaybackReport(key, stop, item, 400000000L, false, report, stopped: true));
        Assert.True(manager.UpdateExternalReport(key, report, stop, stopped: true));
        Assert.False(manager.UpdateExternalReport(key, report, current));
        Assert.Empty(manager.GetActivePlaybackStates(TimeSpan.FromMinutes(5)));
        Assert.Null(manager.GetLastPlayingState(key).ItemId);
    }

    [Fact]
    public async Task ExternalReport_TracksOwnerAndClearsProjectionWhenNativePlaybackStarts()
    {
        var settings = CreateSettings();
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var manager = new JellyfinSessionManager(CreateProxyService(handler, settings),
            Options.Create(settings), NullLogger<JellyfinSessionManager>.Instance);
        var key = Key("same-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Finamp", "Phone", "1", CreateHeaders()));
        var report = JsonNode.Parse("""{"ItemId":"ext-deezer-song-42","Item":{"Id":"ext-deezer-song-42","Name":"External"},"PositionTicks":42,"PlaySessionId":"play-1","PlaylistItemId":"repeat","IsPaused":true,"NowPlayingQueue":[]}""")!.AsObject();
        manager.UpdateExternalReport(key, report);
        report["PositionTicks"] = 99;
        Assert.Equal(42, manager.GetExternalReport(key)!["PositionTicks"]!.GetValue<long>());
        var ghost = JellyfinExternalPlaybackReport.UpstreamId("ext-deezer-song-42");
        var sessions = $$$"""[{"DeviceId":"same-device","UserId":"backend-user","NowPlayingItem":{"Id":"{{{ghost}}}"}},{"DeviceId":"same-device","UserId":"someone-else","NowPlayingItem":{"Id":"{{{ghost}}}"}}]""";
        Assert.Null(manager.ProjectExternalSessions(sessions, "other-backend"));
        using var projected = JsonDocument.Parse(manager.ProjectExternalSessions(sessions, "backend")!);
        Assert.Equal("ext-deezer-song-42", projected.RootElement[0].GetProperty("NowPlayingItem").GetProperty("Id").GetString());
        Assert.Equal(ghost, projected.RootElement[1].GetProperty("NowPlayingItem").GetProperty("Id").GetString());
        manager.UpdatePlayingItem(key, null, null);
        using var stopped = JsonDocument.Parse(manager.ProjectExternalSessions(sessions, "backend")!);
        Assert.False(stopped.RootElement[0].TryGetProperty("NowPlayingItem", out _));
        Assert.Null(manager.GetExternalReport(key));
        manager.UpdatePlayingItem(key, "native-song", 0);
        Assert.Null(manager.ProjectExternalSessions(sessions, "backend"));
    }

    [Fact]
    public async Task ExternalReport_NewOccurrenceDoesNotInheritExplicitStopSuppression()
    {
        var settings = CreateSettings();
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var manager = new JellyfinSessionManager(CreateProxyService(handler, settings),
            Options.Create(settings), NullLogger<JellyfinSessionManager>.Instance);
        var key = Key("same-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Finamp", "Phone", "1", CreateHeaders()));
        var first = JsonNode.Parse("""{"ItemId":"ext-deezer-song-42","PlaylistItemId":"first","PositionTicks":0}""")!.AsObject();
        manager.UpdateExternalReport(key, first);
        manager.MarkExplicitStop(key, "ext-deezer-song-42");
        manager.UpdatePlayingItem(key, null, null);
        var second = (JsonObject)first.DeepClone();
        second["PlaylistItemId"] = "repeat";
        manager.UpdateExternalReport(key, second);
        Assert.False(manager.WasRecentlyExplicitlyStopped(key, "ext-deezer-song-42", TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task RemoveExternalSession_StopsSameUpstreamItemWithSessionAndOccurrence()
    {
        string? stopBody = null;
        var settings = CreateSettings();
        using var handler = new DelegateHttpMessageHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/Sessions/Playing/Stopped")
                stopBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var manager = new JellyfinSessionManager(CreateProxyService(handler, settings),
            Options.Create(settings), NullLogger<JellyfinSessionManager>.Instance);
        var key = Key("same-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Finamp", "Phone", "1", CreateHeaders()));
        manager.UpdateExternalReport(key, JsonNode.Parse("""{"ItemId":"ext-deezer-song-42","Item":{"Id":"ext-deezer-song-42"},"PositionTicks":42,"PlaySessionId":"play-1","PlaylistItemId":"repeat","NowPlayingQueue":[{"Id":"ext-deezer-song-42","PlaylistItemId":"repeat"}]}""")!.AsObject());
        await manager.RemoveSessionAsync(key);
        using var body = JsonDocument.Parse(Assert.IsType<string>(stopBody));
        var ghost = JellyfinExternalPlaybackReport.UpstreamId("ext-deezer-song-42");
        Assert.Equal(ghost, body.RootElement.GetProperty("ItemId").GetString());
        Assert.Equal("play-1", body.RootElement.GetProperty("PlaySessionId").GetString());
        Assert.Equal("repeat", body.RootElement.GetProperty("PlaylistItemId").GetString());
        Assert.Equal(ghost, body.RootElement.GetProperty("NowPlayingQueue")[0].GetProperty("Id").GetString());
        Assert.Null(manager.GetExternalReport(key));
    }
}
