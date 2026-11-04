using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Personal", "personal", false)]
    [InlineData("Shared", "shared", true)]
    [InlineData(null, null, false)]
    public async Task ClientApi_PlaybackStreamReturnsOnlyTheListenersDeviceAndSafeSource(
        string? scope, string? expectedScope, bool cached)
    {
        PlaybackDeliveryActivityStore? activity = null;
        var gateway = DeliveryGateway("flac", "flac");
        gateway.Setup(service => service.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42",
            It.IsAny<ProviderAudioQuality>(), null, It.IsAny<bool>())).ReturnsAsync((ProtocolExecutionContext execution,
                string provider, string externalId, ProviderAudioQuality quality, string? range, bool head) =>
        {
            PlaybackStartTimings.Current!.Select("deezer", cached, scope);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DeliveryBytes("flac")) };
            response.Content.Headers.ContentType = new("audio/flac");
            var stream = new ProtocolProviderStream(response, new ProviderStreamLease("fixture",
                new Uri("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
                DeliveryMedia("flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42",
                IsCached: cached, SelectionReason: "provider-priority");
            activity!.StreamOpened(execution, "ext-deezer-song-42", quality, stream);
            return stream;
        });
        using var factory = ClientPlaybackFactory(gateway);
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services, cached);
        activity = factory.Services.GetRequiredService<PlaybackDeliveryActivityStore>();
        using var streamRequest = ClientPlaybackRequest("/Audio/ext-deezer-song-42/stream");
        using var stream = await client.SendAsync(streamRequest);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var requestId = stream.Headers.GetValues("X-Allstarr-Playback-Request").Single();
        await SetClientPlaybackAsync(factory, "listener", "phone", "ext-deezer-song-42");
        await SeedClientPlaybackAsync(factory, "valid", "Shared");

        string? payload = null;
        foreach (var path in new[] { $"/allstarr/v1/playback/{requestId}", "/allstarr/v1/playback/current" })
        {
            using var request = ClientPlaybackRequest(path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal(["accountScope", "cached", "delivered", "itemId", "provider", "routeReason", "startedAt"],
                root.EnumerateObject().Select(property => property.Name).Order().ToArray());
            Assert.Equal("deezer", root.GetProperty("provider").GetProperty("id").GetString());
            Assert.Equal("Deezer", root.GetProperty("provider").GetProperty("name").GetString());
            Assert.Equal("/allstarr/v1/providers/deezer/icon", root.GetProperty("provider").GetProperty("iconUrl").GetString());
            Assert.Equal(cached ? "/allstarr/v1/providers/deezer/icon?variant=glyph" : null, root.GetProperty("provider").GetProperty("glyphUrl").GetString());
            Assert.Equal(expectedScope, root.GetProperty("accountScope").GetString());
            Assert.Equal(cached, root.GetProperty("cached").GetBoolean());
            Assert.Equal("provider-priority", root.GetProperty("routeReason").GetString());
            Assert.Equal("ext-deezer-song-42", root.GetProperty("itemId").GetString());
            Assert.True(root.GetProperty("startedAt").TryGetDateTimeOffset(out _));
            var delivered = root.GetProperty("delivered");
            Assert.Equal("flac", delivered.GetProperty("format").GetString());
            Assert.Equal("flac", delivered.GetProperty("codec").GetString());
            Assert.Equal(DeliveryMedia("flac", "flac").BitDepth, delivered.GetProperty("bitDepth").GetInt32());
            Assert.Equal(DeliveryMedia("flac", "flac").SampleRate, delivered.GetProperty("sampleRate").GetInt32());
            Assert.Equal(cached ? (int?)null : 456960, delivered.GetProperty("bitrate").Deserialize<int?>());
            if (payload != null) Assert.Equal(payload, body);
            payload = body;
        }
        foreach (var path in new[] { $"/allstarr/v1/playback/{requestId}", "/allstarr/v1/playback/current" })
            foreach (var (user, device) in new[] { ("other", "phone"), ("listener", "tablet"), ("listener", (string?)null) })
            {
                using var request = ClientPlaybackRequest(path, user, device);
                using var response = await client.SendAsync(request);
                await AssertClientApiError(response, 404, "not_found");
            }
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        manager.UpdatePlayingItem(new("primary", "listener", "phone"), null, null);
        using var stoppedRequest = ClientPlaybackRequest("/allstarr/v1/playback/current");
        using var stopped = await client.SendAsync(stoppedRequest);
        await AssertClientApiError(stopped, 404, "not_found");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("legacy")]
    [InlineData("mismatched-owner")]
    public async Task ClientApi_PlaybackUnavailableSamplesHaveTheSameNotFound(string kind)
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        var id = await SeedClientPlaybackAsync(factory, kind);
        using var request = ClientPlaybackRequest($"/allstarr/v1/playback/{id:N}");
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 404, "not_found");
    }

    [Theory]
    [InlineData("Personal", "personal")]
    [InlineData("Shared", "shared")]
    [InlineData("private-account-name", null)]
    public async Task ClientApi_PlaybackMapsScopeAndDeliveredOutputWithoutAccountLabels(string scope, string? expected)
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        var id = await SeedClientPlaybackAsync(factory, "valid", scope);
        await SetClientPlaybackAsync(factory, "listener", "phone", "ext-deezer-song-42");
        foreach (var path in new[] { $"/allstarr/v1/playback/{id:N}", "/allstarr/v1/playback/current" })
        {
            using var request = ClientPlaybackRequest(path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-account-name", body);
            Assert.DoesNotContain("accountLabel", body);
            Assert.DoesNotContain("accountId", body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(expected, json.RootElement.GetProperty("accountScope").GetString());
            var delivered = json.RootElement.GetProperty("delivered");
            Assert.Equal("mp3", delivered.GetProperty("format").GetString());
            Assert.Equal("mp3", delivered.GetProperty("codec").GetString());
            Assert.Equal(128000, delivered.GetProperty("bitrate").GetInt32());
            Assert.Equal(JsonValueKind.Null, delivered.GetProperty("bitDepth").ValueKind);
        }
    }

    [Fact]
    public async Task ClientApi_CurrentPlaybackIncludesNativeLibraryAndKeepsItsStartAcrossProgress()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        using var fixtures = new ProviderIconFiles();
        fixtures.Use(factory.Services);
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get,
            "/Audio/native-song/stream?api_key=listener-token&deviceId=phone");
        using var stream = await client.SendAsync(streamRequest);
        Assert.True(stream.StatusCode == HttpStatusCode.OK, await stream.Content.ReadAsStringAsync());
        Assert.False(stream.Headers.Contains("X-Allstarr-Playback-Request"));
        await SetClientPlaybackAsync(factory, "listener", "phone", "native-song");
        DateTimeOffset? startedAt = null;
        for (var index = 0; index < 2; index++)
        {
            using var request = ClientPlaybackRequest("/allstarr/v1/playback/current");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.Equal("native-song", root.GetProperty("itemId").GetString());
            AssertLibraryProvider(root.GetProperty("provider"));
            Assert.Equal(JsonValueKind.Null, root.GetProperty("accountScope").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("delivered").ValueKind);
            Assert.False(root.GetProperty("cached").GetBoolean());
            Assert.Equal("native-local-library", root.GetProperty("routeReason").GetString());
            var observed = root.GetProperty("startedAt").GetDateTimeOffset();
            if (startedAt.HasValue) Assert.Equal(startedAt, observed);
            startedAt = observed;
            await SetClientPlaybackAsync(factory, "listener", "phone", "native-song", 1000);
        }
        using var otherRequest = ClientPlaybackRequest("/allstarr/v1/playback/current", device: "tablet");
        using var other = await client.SendAsync(otherRequest);
        await AssertClientApiError(other, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_PlaybackSamplesIdentifyTheLibraryProvider()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        using var fixtures = new ProviderIconFiles();
        fixtures.Use(factory.Services);
        var id = await SeedClientPlaybackAsync(factory, "local");
        await SetClientPlaybackAsync(factory, "listener", "phone", "native-song");
        foreach (var path in new[] { $"/allstarr/v1/playback/{id:N}", "/allstarr/v1/playback/current" })
        {
            using var request = ClientPlaybackRequest(path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertLibraryProvider(json.RootElement.GetProperty("provider"));
        }
    }

    [Theory]
    [InlineData("/allstarr/v1/playback/current")]
    [InlineData("/allstarr/v1/playback/00000000000000000000000000000001")]
    public async Task ClientApi_PlaybackRoutesAreNotFoundWhileDisabled(string path)
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory, enabled: false);
        using var response = await client.GetAsync(path);
        await AssertClientApiError(response, 404, "not_found");
    }

    private static ProtocolFactory ClientPlaybackFactory(Mock<IProtocolProviderGateway>? gateway = null) => ClientApiFactory(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/Users/Me")
        {
            var other = request.Headers.TryGetValues("Authorization", out var values) &&
                values.Any(value => value.Contains("other-token", StringComparison.Ordinal));
            return Json(200, other ? """{"Id":"other"}""" : """{"Id":"listener"}""");
        }
        if (path == "/Items") return Json(200, """{"Items":[{"Id":"native-song","Type":"Audio"}]}""");
        if (path == "/Audio/native-song/stream") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}");
    }, services =>
    {
        if (gateway == null) return;
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway.Object);
    });

    private static HttpRequestMessage ClientPlaybackRequest(string path, string user = "listener", string? device = "phone")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", device == null
            ? $"MediaBrowser Token=\"{user}-token\""
            : $"MediaBrowser Client=\"Fixture\", DeviceId=\"{device}\", Token=\"{user}-token\"");
        return request;
    }

    private static async Task SetClientPlaybackAsync(ProtocolFactory factory, string user, string device, string item, long position = 0)
    {
        var principal = await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", user));
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        var key = new JellyfinSessionKey("primary", user, device);
        await manager.RegisterProxiedWebSocketAsync(key);
        Assert.True(await manager.EnsureSessionAsync(key, "Fixture", "Fixture", "1", new HeaderDictionary(), principal));
        manager.UpdatePlayingItem(key, item, position);
    }

    private static async Task<Guid> SeedClientPlaybackAsync(ProtocolFactory factory, string kind, string scope = "Personal")
    {
        var principal = await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener"));
        using var timing = new PlaybackStartTimings(TimeProvider.System);
        timing.Enable();
        timing.BeginPlay(new(ProtocolKind.Jellyfin, "primary", "listener", principal, "fixture",
            DateTimeOffset.UtcNow.AddMinutes(1), default, new(deviceId: "phone")), kind == "local" ? "native-song" : "ext-deezer-song-42");
        timing.Select("deezer", true, scope);
        timing.Route("deezer", true, "provider-priority", "Any", "Personal");
        timing.AccountLabel("private-account-name");
        timing.Receive(new(DeliveryMedia("flac", "flac")));
        var output = new ProviderMediaFormat("audio/mpeg", "mp3", "mp3", 128000, 44100);
        var sample = timing.Snapshot("completed");
        sample = sample with
        {
            StartedAt = kind == "expired" ? DateTimeOffset.UtcNow.AddDays(-8) : sample.StartedAt,
            Trace = sample.Trace! with { Delivery = new(null, null, null, null, true, "mp3", "mp3", 0, [], [], output) }
        };
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(sample))!;
        if (kind == "legacy") json.AsObject().Remove("DeviceId");
        if (kind == "mismatched-owner") json["Trace"]!["OwnerUserId"] = Guid.NewGuid().ToString();
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        db.AuditEvents.Add(new()
        {
            Id = sample.Id,
            ActorUserId = principal!.UserId,
            Category = PlaybackStartTimingStore.Category,
            SubjectType = PlaybackStartTimingStore.Category,
            SubjectId = "recent",
            CreatedAt = sample.StartedAt,
            DetailsJson = json.ToJsonString()
        });
        await db.SaveChangesAsync();
        return kind == "unknown" ? Guid.NewGuid() : sample.Id;
    }
}
