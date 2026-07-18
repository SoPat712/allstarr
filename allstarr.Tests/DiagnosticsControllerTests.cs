using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Services.Spotify;
using allstarr.Services.SquidWTF;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class DiagnosticsControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));

    public DiagnosticsControllerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app"));
    }

    [Fact]
    public async Task Status_DoesNotReturnConfiguredJellyfinAddress()
    {
        const string privateJellyfinUrl = "http://private-jellyfin.internal:8096";
        var controller = CreateController(
            [],
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new JellyfinSettings { Url = privateJellyfinUrl });

        var result = Assert.IsType<OkObjectResult>(await controller.GetStatus());
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("Configured", json, StringComparison.Ordinal);
        Assert.DoesNotContain(privateJellyfinUrl, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-jellyfin.internal", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaProbe_VerifiesMetadataAndArtworkThroughInternalProxy()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/Images/Primary", StringComparison.Ordinal) == true)
            {
                var imageResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9])
                };
                imageResponse.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                return imageResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"Items\":[{\"Id\":\"track-1\",\"ImageTags\":{\"Primary\":\"art-v1\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var settings = new JellyfinSettings
        {
            Url = "http://jellyfin.example.test:8096",
            ApiKey = "server-api-key",
            UserId = "user-1"
        };
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));
        var cache = new RedisCacheService(
            Options.Create(new RedisSettings { Enabled = false }),
            NullLogger<RedisCacheService>.Instance);
        var proxy = new JellyfinProxyService(
            httpClientFactory.Object,
            Options.Create(settings),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            NullLogger<JellyfinProxyService>.Instance,
            cache,
            new ConfigurationBuilder().Build());
        var services = new ServiceCollection()
            .AddSingleton(proxy)
            .BuildServiceProvider();
        var controller = CreateController([], handler, settings, services);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session-1",
            UserId = "user-1",
            UserName = "fixture",
            IsAdministrator = true,
            JellyfinAccessToken = "player-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        var result = Assert.IsType<OkObjectResult>(
            await controller.ProbeMediaPipeline());
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("media_pipeline_healthy", json, StringComparison.Ordinal);
        Assert.Contains("image/jpeg", json, StringComparison.Ordinal);
        Assert.Contains("\"bytes\":4", json, StringComparison.Ordinal);
        Assert.Contains("authenticated player artwork", json, StringComparison.Ordinal);
        Assert.Contains("\"tested\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"available\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("track-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("server-api-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("player-token", json, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public void SquidWtfBaseUrl_ReturnsOnlyTheSameOriginProxyRoute()
    {
        const string upstream = "https://api.example.test/private-base";
        var controller = CreateController(
            [upstream],
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = Assert.IsType<OkObjectResult>(controller.GetSquidWtfBaseUrl());
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("/api/admin/squidwtf-browser-proxy", json, StringComparison.Ordinal);
        Assert.DoesNotContain(upstream, json, StringComparison.Ordinal);
        Assert.DoesNotContain("api.example.test", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SquidWtfSearch_UsesKnownEndpointAndEncodesTheSearchValue()
    {
        Uri? requestedUri = null;
        var handler = new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"items\":[{\"id\":\"fixture\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var controller = CreateController(
            ["https://api.example.test/base"],
            handler);

        var result = Assert.IsType<JsonResult>(
            await controller.SearchSquidWtf(" artist & title "));

        Assert.NotNull(requestedUri);
        Assert.Equal("api.example.test", requestedUri.Host);
        Assert.Equal("/base/search/", requestedUri.AbsolutePath);
        Assert.Equal("?s=artist%20%26%20title", requestedUri.Query);
        Assert.Contains("fixture", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SquidWtfSearch_RejectsPrivateCatalogTargetBeforeSendingRequest()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var controller = CreateController(["http://127.0.0.1:8080"], handler);

        var result = Assert.IsType<ObjectResult>(
            await controller.SearchSquidWtf("fixture"));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.Equal(0, handler.RequestCount);
        Assert.DoesNotContain("127.0.0.1", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SquidWtfSearch_RejectsDeclaredOversizeResponse()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
            response.Content.Headers.ContentLength = (2 * 1024 * 1024) + 1;
            return response;
        });
        var controller = CreateController(["https://api.example.test"], handler);

        var result = Assert.IsType<ObjectResult>(
            await controller.SearchSquidWtf("fixture"));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.Contains("size limit", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SquidWtfSearch_DoesNotFollowRedirectToPrivateTarget()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/private-json");
            return response;
        });
        var controller = CreateController(["https://api.example.test"], handler);

        var result = Assert.IsType<ObjectResult>(
            await controller.SearchSquidWtf("fixture"));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain("127.0.0.1", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicEndpointConnector_RejectsMixedPrivateDnsResultsBeforeConnect()
    {
        var dns = new QueueDnsResolver(
            [IPAddress.Parse("93.184.216.34"), IPAddress.Loopback]);
        var socket = new RecordingIpConnector();
        var connector = new PublicEndpointConnector(dns, socket);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            connector.ConnectAsync("api.example.test", 443, CancellationToken.None).AsTask());

        Assert.Equal(1, dns.ResolveCount);
        Assert.Equal(0, socket.ConnectCount);
    }

    [Fact]
    public async Task PublicEndpointConnector_ReResolvesAndBlocksDnsRebinding()
    {
        var publicAddress = IPAddress.Parse("93.184.216.34");
        var dns = new QueueDnsResolver([publicAddress], [IPAddress.Loopback]);
        var socket = new RecordingIpConnector();
        var connector = new PublicEndpointConnector(dns, socket);

        await using (var stream = await connector.ConnectAsync(
                         "api.example.test",
                         443,
                         CancellationToken.None))
        {
        }

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            connector.ConnectAsync("api.example.test", 443, CancellationToken.None).AsTask());

        Assert.Equal(2, dns.ResolveCount);
        Assert.Equal(1, socket.ConnectCount);
        Assert.Equal(publicAddress, Assert.Single(socket.Addresses));
    }

    [Fact]
    public void SafeProxyProductionTransport_DisablesRedirectsCookiesAndEnvironmentProxy()
    {
        var connector = new PublicEndpointConnector(
            new QueueDnsResolver([IPAddress.Parse("93.184.216.34")]),
            new RecordingIpConnector());
        using var handler = Assert.IsType<SocketsHttpHandler>(
            new SafeProxyTransportFactory(connector).CreateHandler());

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
    }

    private DiagnosticsController CreateController(
        List<string> squidWtfApiUrls,
        HttpMessageHandler handler,
        JellyfinSettings? jellyfinSettings = null,
        IServiceProvider? requestServices = null)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns("Development");
        environment.SetupGet(item => item.ContentRootPath).Returns(Path.Combine(_root, "app"));
        var adminHelper = new AdminHelperService(
            NullLogger<AdminHelperService>.Instance,
            Options.Create(jellyfinSettings ?? new JellyfinSettings()),
            environment.Object);
        var spotifySettings = new SpotifyApiSettings();
        var cookieService = new SpotifySessionCookieService(
            Options.Create(spotifySettings),
            adminHelper,
            NullLogger<SpotifySessionCookieService>.Instance);
        var redis = new RedisCacheService(
            Options.Create(new RedisSettings { Enabled = false }),
            NullLogger<RedisCacheService>.Instance);
        var storageOptions = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = "Data Source=:memory:"
        };
        var storageState = new DurableStorageState(storageOptions);
        storageState.Set(DurableStorageReadiness.Ready, "fixture");
        var safeProxyClient = new SafeJsonProxyClient(
            new StubTransportFactory(handler));
        var controller = new DiagnosticsController(
            NullLogger<DiagnosticsController>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Type"] = "Jellyfin"
            }).Build(),
            Options.Create(spotifySettings),
            Options.Create(new SpotifyImportSettings()),
            Options.Create(jellyfinSettings ?? new JellyfinSettings()),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            Options.Create(new SquidWTFSettings()),
            cookieService,
            new SquidWtfEndpointCatalog(squidWtfApiUrls, []),
            redis,
            storageState,
            safeProxyClient)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = requestServices ?? new ServiceCollection().BuildServiceProvider()
                }
            }
        };
        return controller;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class StubTransportFactory(HttpMessageHandler handler)
        : ISafeProxyTransportFactory
    {
        public HttpMessageHandler CreateHandler() => handler;
    }

    private sealed class QueueDnsResolver(params IReadOnlyList<IPAddress>[] results)
        : IPublicEndpointDnsResolver
    {
        private readonly Queue<IReadOnlyList<IPAddress>> _results = new(results);

        public int ResolveCount { get; private set; }

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingIpConnector : IResolvedIpConnector
    {
        public int ConnectCount { get; private set; }
        public List<IPAddress> Addresses { get; } = [];

        public ValueTask<Stream> ConnectAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            Addresses.Add(address);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }
    }
}
