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
