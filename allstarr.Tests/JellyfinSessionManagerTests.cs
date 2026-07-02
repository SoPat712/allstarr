using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;

namespace allstarr.Tests;

public class JellyfinSessionManagerTests
{
    [Fact]
    public async Task MarkSessionPotentiallyEnded_DoesNotAutoRemoveSession()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        var settings = new JellyfinSettings
        {
            Url = "http://127.0.0.1:1",
            ApiKey = "server-api-key",
            ClientName = "Allstarr",
            DeviceName = "Allstarr",
            DeviceId = "allstarr",
            ClientVersion = "1.0"
        };

        var proxyService = CreateProxyService(handler, settings);
        using var manager = new JellyfinSessionManager(
            proxyService,
            Options.Create(settings),
            NullLogger<JellyfinSessionManager>.Instance);

        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] =
                "MediaBrowser Client=\"Feishin\", Device=\"Desktop\", DeviceId=\"dev-123\", Version=\"1.0\", Token=\"abc\""
        };

        var ensured = await manager.EnsureSessionAsync("dev-123", "Feishin", "Desktop", "1.0", headers);
        Assert.True(ensured);

        manager.MarkSessionPotentiallyEnded("dev-123", TimeSpan.FromMilliseconds(25));
        await Task.Delay(100);

        Assert.True(manager.HasSession("dev-123"));
    }

    [Fact]
    public async Task RemoveSessionAsync_ReportsPlaybackStopButDoesNotLogoutUserSession()
    {
        var requestedPaths = new ConcurrentBag<string>();
        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            requestedPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        var settings = new JellyfinSettings
        {
            Url = "http://127.0.0.1:1",
            ApiKey = "server-api-key",
            ClientName = "Allstarr",
            DeviceName = "Allstarr",
            DeviceId = "allstarr",
            ClientVersion = "1.0"
        };

        var proxyService = CreateProxyService(handler, settings);
        using var manager = new JellyfinSessionManager(
            proxyService,
            Options.Create(settings),
            NullLogger<JellyfinSessionManager>.Instance);

        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] =
                "MediaBrowser Client=\"Feishin\", Device=\"Desktop\", DeviceId=\"dev-123\", Version=\"1.0\", Token=\"abc\""
        };

        var ensured = await manager.EnsureSessionAsync("dev-123", "Feishin", "Desktop", "1.0", headers);
        Assert.True(ensured);

        manager.UpdatePlayingItem("dev-123", "item-123", 42);
        await manager.RemoveSessionAsync("dev-123");

        Assert.Contains("/Sessions/Capabilities/Full", requestedPaths);
        Assert.Contains("/Sessions/Playing/Stopped", requestedPaths);
        Assert.DoesNotContain("/Sessions/Logout", requestedPaths);
    }

    [Fact]
    public async Task GetActivePlaybackStates_ReturnsTrackedPlayingItems()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        var settings = new JellyfinSettings
        {
            Url = "http://127.0.0.1:1",
            ApiKey = "server-api-key",
            ClientName = "Allstarr",
            DeviceName = "Allstarr",
            DeviceId = "allstarr",
            ClientVersion = "1.0"
        };

        var proxyService = CreateProxyService(handler, settings);
        using var manager = new JellyfinSessionManager(
            proxyService,
            Options.Create(settings),
            NullLogger<JellyfinSessionManager>.Instance);

        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] =
                "MediaBrowser Client=\"Feishin\", Device=\"Desktop\", DeviceId=\"dev-123\", Version=\"1.0\", Token=\"abc\""
        };

        var ensured = await manager.EnsureSessionAsync("dev-123", "Feishin", "Desktop", "1.0", headers);
        Assert.True(ensured);

        manager.UpdatePlayingItem("dev-123", "ext-squidwtf-song-35734823", 45 * TimeSpan.TicksPerSecond);

        var states = manager.GetActivePlaybackStates(TimeSpan.FromMinutes(1));

        var state = Assert.Single(states);
        Assert.Equal("dev-123", state.DeviceId);
        Assert.Equal("ext-squidwtf-song-35734823", state.ItemId);
        Assert.Equal(45 * TimeSpan.TicksPerSecond, state.PositionTicks);
    }

    [Fact]
    public async Task EnsureSessionAsync_WithProxiedWebSocket_DoesNotPostSyntheticCapabilities()
    {
        var requestedPaths = new ConcurrentBag<string>();
        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            requestedPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        var settings = new JellyfinSettings
        {
            Url = "http://127.0.0.1:1",
            ApiKey = "server-api-key",
            ClientName = "Allstarr",
            DeviceName = "Allstarr",
            DeviceId = "allstarr",
            ClientVersion = "1.0"
        };

        var proxyService = CreateProxyService(handler, settings);
        using var manager = new JellyfinSessionManager(
            proxyService,
            Options.Create(settings),
            NullLogger<JellyfinSessionManager>.Instance);

        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] =
                "MediaBrowser Client=\"Finamp\", Device=\"Android Auto\", DeviceId=\"dev-123\", Version=\"1.0\", Token=\"abc\""
        };

        await manager.RegisterProxiedWebSocketAsync("dev-123");

        var ensured = await manager.EnsureSessionAsync("dev-123", "Finamp", "Android Auto", "1.0", headers);

        Assert.True(ensured);
        Assert.DoesNotContain("/Sessions/Capabilities/Full", requestedPaths);
    }

    private static JellyfinProxyService CreateProxyService(HttpMessageHandler handler, JellyfinSettings settings)
    {
        var httpClientFactory = new TestHttpClientFactory(handler);
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };

        var cache = new RedisCacheService(
            Options.Create(new RedisSettings { Enabled = false }),
            NullLogger<RedisCacheService>.Instance);

        return new JellyfinProxyService(
            httpClientFactory,
            Options.Create(settings),
            httpContextAccessor,
            NullLogger<JellyfinProxyService>.Instance,
            cache,
            new ConfigurationBuilder().Build());
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler, disposeHandler: false);
        }

        public HttpClient CreateClient(string name)
        {
            return _client;
        }
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
