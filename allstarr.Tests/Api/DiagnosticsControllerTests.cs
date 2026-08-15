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
            new JellyfinSettings { Url = privateJellyfinUrl });

        var result = Assert.IsType<OkObjectResult>(await controller.GetStatus());
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("Configured", json, StringComparison.Ordinal);
        Assert.DoesNotContain(privateJellyfinUrl, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-jellyfin.internal", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScrobblingSessions_RequiresDurableCheckpointStore()
    {
        var controller = CreateController();

        var result = Assert.IsType<BadRequestObjectResult>(
            await controller.GetScrobblingSessions(CancellationToken.None));

        Assert.Contains("Durable scrobble status", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaProbe_VerifiesMetadataAndArtworkThroughInternalProxy()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/Audio/", StringComparison.Ordinal) == true)
            {
                Assert.NotNull(request.Headers.Range);
                Assert.Equal("bytes=0-65535", request.Headers.Range!.ToString());
                var audioResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent([0x66, 0x4C, 0x61, 0x43, 0x00, 0x00])
                };
                audioResponse.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("audio/flac");
                return audioResponse;
            }

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
        var cache = new DisabledApplicationCache();
        var proxy = new JellyfinProxyService(
            httpClientFactory.Object,
            Options.Create(settings),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            NullLogger<JellyfinProxyService>.Instance,
            cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance),
            new ConfigurationBuilder().Build());
        var services = new ServiceCollection()
            .AddSingleton(proxy)
            .BuildServiceProvider();
        var controller = CreateController(settings, services);
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
        Assert.Contains("authenticated player artwork, and audio streaming", json, StringComparison.Ordinal);
        Assert.Contains("audio/flac", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":206", json, StringComparison.Ordinal);
        Assert.Contains("\"bytes\":6", json, StringComparison.Ordinal);
        Assert.Contains("\"tested\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"available\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("track-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("server-api-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("player-token", json, StringComparison.Ordinal);
        Assert.Equal(4, handler.RequestCount);
    }

    private DiagnosticsController CreateController(
        JellyfinSettings? jellyfinSettings = null,
        IServiceProvider? requestServices = null)
    {
        var spotifySettings = new SpotifyApiSettings();
        var cookieService = new SpotifySessionCookieService(Options.Create(spotifySettings));
        var storageOptions = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = "Host=database;Database=allstarr;Username=allstarr;Password=not-used"
        };
        var storageState = new DurableStorageState(storageOptions);
        storageState.Set(DurableStorageReadiness.Ready, "fixture");
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
            cookieService,
            storageState)
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

}
