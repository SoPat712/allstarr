using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Controllers;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinExternalPlayback_ForwardsProviderMetadataAcrossStartProgressAndStop()
    {
        const string userId = "fixture-user";
        const string deviceId = "fixture-device";
        var forwarded = new List<(string Path, string Body)>();
        var diagnostics = new List<string>();
        var observedContexts = new List<ProtocolExecutionContext>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "deezer", "42"))
            .Callback<ProtocolExecutionContext, string, string>((context, _, _) => observedContexts.Add(context))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-42",
                ExternalProvider = "deezer",
                ExternalId = "42",
                Title = "Provider Title",
                Artist = "Lead Artist",
                Artists = [" Lead Artist ", "Guest Artist", "  Lead Artist  "],
                Album = "Provider Album",
                AlbumArtist = null,
                Duration = 999,
                PreciseDuration = TimeSpan.FromTicks(12_345_678),
                Track = 7
            });

        using var factory = PlaybackMetadataFactory(gateway, forwarded, userId, diagnostics);
        await PreparePlaybackSessionAsync(factory, userId, deviceId);
        using var client = factory.CreateClient();

        using var start = await client.SendAsync(PlaybackRequest(
            "/Sessions/Playing",
            """{"ItemId":"ext-deezer-song-42","ItemName":"Forged client title","Item":{"Id":"forged-item","Name":"Forged item","Artists":["Forged artist"]},"PositionTicks":12500000,"PlaySessionId":"play-1"}""",
            deviceId));
        using var progress = await client.SendAsync(PlaybackRequest(
            "/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-song-42","Item":{"Id":"forged-progress","Name":"Forged progress"},"PositionTicks":25000000,"PlaySessionId":"play-1"}""",
            deviceId));
        using var stop = await client.SendAsync(PlaybackRequest(
            "/Sessions/Playing/Stopped",
            """{"ItemId":"ext-deezer-song-42","Item":{"Id":"forged-stop","Name":"Forged stop"},"PositionTicks":37500000,"PlaySessionId":"play-1"}""",
            deviceId));

        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);

        Assert.True(forwarded.Count > 0, string.Join(Environment.NewLine, diagnostics));
        var startPayload = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing"));
        var progressPayload = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing/Progress"));
        var stopPayload = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing/Stopped"));
        var ghostId = startPayload.GetProperty("ItemId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ghostId));
        Assert.Equal(ghostId, progressPayload.GetProperty("ItemId").GetString());
        Assert.Equal(ghostId, stopPayload.GetProperty("ItemId").GetString());

        foreach (var payload in new[] { startPayload, progressPayload, stopPayload })
        {
            var item = payload.GetProperty("Item");
            Assert.Equal(ghostId, item.GetProperty("Id").GetString());
            Assert.Equal("Audio", item.GetProperty("Type").GetString());
            Assert.Equal("Audio", item.GetProperty("MediaType").GetString());
            Assert.Equal("Provider Title", item.GetProperty("Name").GetString());
            Assert.Equal(["Lead Artist", "Guest Artist"], item.GetProperty("Artists")
                .EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray());
            Assert.Equal("Provider Album", item.GetProperty("Album").GetString());
            Assert.Equal("Lead Artist", item.GetProperty("AlbumArtist").GetString());
            Assert.Equal(12_345_678, item.GetProperty("RunTimeTicks").GetInt64());
            Assert.Equal(7, item.GetProperty("IndexNumber").GetInt32());
        }

        gateway.Verify(service => service.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(),
            "deezer", "42"), Times.Exactly(3));
        Assert.NotEmpty(observedContexts);
        Assert.All(observedContexts, context =>
        {
            Assert.Equal(ProtocolKind.Jellyfin, context.Protocol);
            Assert.Equal(userId, context.VerifiedBackendPrincipalId);
            Assert.NotNull(context.Actor);
        });
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinExternalProgress_ReusesOneProviderLookupForInferredStartAndProgress()
    {
        const string userId = "fixture-user";
        const string deviceId = "fixture-device";
        const string previousId = "ext-deezer-previous";
        const string currentId = "ext-deezer-current";
        var forwarded = new List<(string Path, string Body)>();
        var diagnostics = new List<string>();
        var observedContexts = new List<ProtocolExecutionContext>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "deezer", "previous"))
            .Callback<ProtocolExecutionContext, string, string>((context, _, _) => observedContexts.Add(context))
            .ReturnsAsync(PlaybackSong(previousId, "Previous title"));
        gateway.Setup(service => service.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "deezer", "current"))
            .Callback<ProtocolExecutionContext, string, string>((context, _, _) => observedContexts.Add(context))
            .ReturnsAsync(PlaybackSong(currentId, "Current title"));

        using var factory = PlaybackMetadataFactory(gateway, forwarded, userId, diagnostics);
        var key = await PreparePlaybackSessionAsync(factory, userId, deviceId);
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        manager.UpdatePlayingItem(key, previousId, TimeSpan.FromSeconds(4).Ticks);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(PlaybackRequest(
            "/Sessions/Playing/Progress",
            """{"ItemId":"ext-deezer-current","Item":{"Id":"forged-current"},"PositionTicks":25000000,"PlaySessionId":"play-2"}""",
            deviceId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(forwarded.Count > 0, string.Join(Environment.NewLine, diagnostics));
        var inferredStop = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing/Stopped"));
        var inferredStart = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing"));
        var currentProgress = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing/Progress"));
        Assert.False(string.IsNullOrWhiteSpace(inferredStop.GetProperty("ItemId").GetString()));
        Assert.Equal("Previous title", inferredStop.GetProperty("Item").GetProperty("Name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(inferredStart.GetProperty("ItemId").GetString()));
        Assert.Equal(inferredStart.GetProperty("ItemId").GetString(), currentProgress.GetProperty("ItemId").GetString());
        Assert.Equal("Current title", inferredStart.GetProperty("Item").GetProperty("Name").GetString());
        Assert.Equal("Current title", currentProgress.GetProperty("Item").GetProperty("Name").GetString());

        gateway.Verify(service => service.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(),
            "deezer", "previous"), Times.Once);
        gateway.Verify(service => service.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(),
            "deezer", "current"), Times.Once);
        Assert.NotEmpty(observedContexts);
        Assert.All(observedContexts, context =>
        {
            Assert.Equal(ProtocolKind.Jellyfin, context.Protocol);
            Assert.Equal(userId, context.VerifiedBackendPrincipalId);
            Assert.NotNull(context.Actor);
        });
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinExternalPlayback_MetadataMissUsesSafeGenericItemInsteadOfClientMetadata()
    {
        const string userId = "fixture-user";
        const string deviceId = "fixture-device";
        var forwarded = new List<(string Path, string Body)>();
        var diagnostics = new List<string>();
        var observedContexts = new List<ProtocolExecutionContext>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(
                It.IsAny<ProtocolExecutionContext>(),
                "deezer", "missing"))
            .Callback<ProtocolExecutionContext, string, string>((context, _, _) => observedContexts.Add(context))
            .ReturnsAsync((Song?)null);

        using var factory = PlaybackMetadataFactory(gateway, forwarded, userId, diagnostics);
        await PreparePlaybackSessionAsync(factory, userId, deviceId);
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(PlaybackRequest(
            "/Sessions/Playing",
            """{"ItemId":"ext-deezer-missing","ItemName":"Forged title","Item":{"Id":"forged","Name":"Forged title","Artists":["Forged"]},"PositionTicks":12500000}""",
            deviceId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(forwarded.Count > 0, string.Join(Environment.NewLine, diagnostics));
        var payload = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing"));
        var item = payload.GetProperty("Item");
        Assert.Equal(payload.GetProperty("ItemId").GetString(), item.GetProperty("Id").GetString());
        Assert.Equal("Unknown", item.GetProperty("Name").GetString());
        Assert.Equal(["Unknown artist"], item.GetProperty("Artists").EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty).ToArray());
        Assert.Equal("Unknown album", item.GetProperty("Album").GetString());
        Assert.Equal("Unknown artist", item.GetProperty("AlbumArtist").GetString());
        Assert.DoesNotContain("Forged", item.GetRawText(), StringComparison.Ordinal);
        gateway.Verify(service => service.GetSongAsync(
            It.IsAny<ProtocolExecutionContext>(),
            "deezer", "missing"), Times.Once);
        Assert.NotEmpty(observedContexts);
        Assert.All(observedContexts, context =>
        {
            Assert.Equal(ProtocolKind.Jellyfin, context.Protocol);
            Assert.Equal(userId, context.VerifiedBackendPrincipalId);
            Assert.NotNull(context.Actor);
        });
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JellyfinNativePlaybackStart_RetainsMinimalPayloadWithoutSynthesizedItem()
    {
        const string userId = "fixture-user";
        const string deviceId = "fixture-device";
        var forwarded = new List<(string Path, string Body)>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                if (request.RequestUri!.AbsolutePath == "/Users/Me")
                    return Json(200, $$"""{"Id":"{{userId}}","Name":"Fixture User"}""");
                if (request.RequestUri.AbsolutePath == "/Items/native-song")
                    return Json(200, """{"Id":"native-song","Type":"Audio","Name":"Native song"}""");
                if (request.RequestUri.AbsolutePath == "/Sessions/Playing")
                {
                    forwarded.Add((request.RequestUri.AbsolutePath,
                        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
                    return Json(204, "{}");
                }
                if (request.RequestUri.AbsolutePath.StartsWith("/Sessions/Capabilities", StringComparison.Ordinal))
                    return Json(204, "{}");
                throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
            },
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        await PreparePlaybackSessionAsync(factory, userId, deviceId);
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(PlaybackRequest(
            "/Sessions/Playing",
            """{"ItemId":"native-song","Item":{"Id":"forged","Name":"Forged"},"PositionTicks":12500000}""",
            deviceId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var payload = PlaybackBody(AssertForwarded(forwarded, "/Sessions/Playing"));
        Assert.Equal(["ItemId", "PositionTicks"], payload.EnumerateObject()
            .Select(property => property.Name).OrderBy(name => name).ToArray());
        Assert.Equal("native-song", payload.GetProperty("ItemId").GetString());
        Assert.Equal(12_500_000, payload.GetProperty("PositionTicks").GetInt64());
        gateway.VerifyNoOtherCalls();
    }

    private static ProtocolFactory PlaybackMetadataFactory(
        Mock<IProtocolProviderGateway> gateway,
        List<(string Path, string Body)> forwarded,
        string userId,
        List<string>? diagnostics = null)
    {
        diagnostics ??= [];
        return new(
            "Jellyfin",
            request =>
            {
                if (request.RequestUri!.AbsolutePath == "/Users/Me")
                    return Json(200, $$"""{"Id":"{{userId}}","Name":"Fixture User"}""");
                if (request.RequestUri.AbsolutePath is "/Sessions/Playing" or
                    "/Sessions/Playing/Progress" or "/Sessions/Playing/Stopped")
                {
                    forwarded.Add((request.RequestUri.AbsolutePath,
                        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
                    return Json(204, "{}");
                }
                if (request.RequestUri.AbsolutePath.StartsWith("/Sessions/Capabilities", StringComparison.Ordinal))
                    return Json(204, "{}");
                throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
            },
            services =>
            {
                AddArtworkPrincipal(services, userId);
                services.AddSingleton<ILogger<JellyfinController>>(
                    new PlaybackCapturingLogger(diagnostics));
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
    }

    private static async Task<JellyfinSessionKey> PreparePlaybackSessionAsync(
        ProtocolFactory factory,
        string userId,
        string deviceId)
    {
        var backend = factory.Services.GetRequiredService<IdentityOptions>().BackendInstanceId;
        var key = new JellyfinSessionKey(backend, userId, deviceId);
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(
            key, "Fixture", "Fixture", "1", new HeaderDictionary()));
        return key;
    }

    private static HttpRequestMessage PlaybackRequest(string path, string body, string deviceId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{path}?api_key=fixture-key&DeviceId={Uri.EscapeDataString(deviceId)}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"MediaBrowser Client=\"Fixture\", Device=\"Fixture\", DeviceId=\"{deviceId}\", Version=\"1\", Token=\"fixture-key\"");
        return request;
    }

    private static (string Path, string Body) AssertForwarded(
        List<(string Path, string Body)> forwarded,
        string path)
    {
        var matches = forwarded.Where(item => item.Path == path).ToArray();
        Assert.Single(matches);
        return matches[0];
    }

    private static JsonElement PlaybackBody((string Path, string Body) forwarded)
    {
        using var document = JsonDocument.Parse(forwarded.Body);
        return document.RootElement.Clone();
    }

    private static Song PlaybackSong(string id, string title) => new()
    {
        Id = id,
        ExternalProvider = "deezer",
        ExternalId = id["ext-deezer-".Length..],
        Title = title,
        Artist = "Fixture Artist",
        Artists = ["Fixture Artist"],
        Album = "Fixture Album",
        PreciseDuration = TimeSpan.FromSeconds(180),
        Duration = 180
    };

    private sealed class PlaybackCapturingLogger(List<string> diagnostics) : ILogger<JellyfinController>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error)
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception != null)
            {
                message = $"{message} ({exception.GetType().Name}: {exception.Message})";
            }

            diagnostics.Add(message);
        }
    }

}
