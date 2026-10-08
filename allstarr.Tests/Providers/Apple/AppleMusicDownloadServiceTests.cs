using System.Net;
using System.Net.Http.Headers;
using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AppleMusicDownloadServiceTests
{
    [Fact]
    public async Task CompletedCacheReachesFirstByteWithoutProviderOrMetadataWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"allstarr-apple-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "track.flac");
            await File.WriteAllBytesAsync(path, [0x66, 0x4c, 0x61, 0x43]);
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync("apple-download", "track-1"))
                .ReturnsAsync(path);
            var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
            var discovery = new Mock<IAppleDownloadEndpointDiscovery>(MockBehavior.Strict);
            var handler = new StubHandler(_ => throw new InvalidOperationException("cache hit must not request"));
            var service = CreateService(directory, local.Object, metadata.Object, discovery.Object, handler);

            await using var stream = await service.DownloadAndStreamAsync("apple-download", "track-1");

            Assert.Equal(0x66, stream.ReadByte());
            Assert.Equal(0, handler.RequestCount);
            local.VerifyAll();
            metadata.VerifyNoOtherCalls();
            discovery.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ClientQualityOverrideDoesNotReuseOrPublishCanonicalCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"allstarr-apple-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
            var discovery = new Mock<IAppleDownloadEndpointDiscovery>(MockBehavior.Strict);
            var handler = new StubHandler(request =>
            {
                Assert.Equal("/api/stream/track-1", request.RequestUri!.AbsolutePath);
                Assert.Equal("?quality=aac-96", request.RequestUri.Query);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x66, 0x4c, 0x61, 0x43])
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
                return response;
            });
            var service = CreateService(directory, local.Object, metadata.Object, discovery.Object, handler);

            await using var stream = await service.DownloadAndStreamAsync(
                "apple-download", "track-1", StreamQuality.Low);
            await stream.CopyToAsync(Stream.Null);

            Assert.Equal(1, handler.RequestCount);
            Assert.Empty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
            local.VerifyNoOtherCalls();
            metadata.VerifyNoOtherCalls();
            discovery.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task AbandonedProgressivePlaybackDeletesPartialWithoutPublishingCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"allstarr-apple-abort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync("apple-download", "track-1"))
                .ReturnsAsync((string?)null);
            var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
            metadata.Setup(item => item.GetSongAsync(
                    "applemusic", "track-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new allstarr.Models.Domain.Song
                {
                    ExternalProvider = "apple-download",
                    ExternalId = "track-1",
                    Title = "Track",
                    Artist = "Artist",
                    Album = "Album"
                });
            var discovery = new Mock<IAppleDownloadEndpointDiscovery>(MockBehavior.Strict);
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x66, 0x4c, 0x61, 0x43])
            });
            var service = CreateService(directory, local.Object, metadata.Object, discovery.Object, handler);

            await using (var stream = await service.DownloadAndStreamAsync("apple-download", "track-1"))
            {
                Assert.Equal(0x66, stream.ReadByte());
            }

            Assert.Empty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
            local.VerifyAll();
            local.Verify(item => item.RegisterDownloadedSongAsync(
                It.IsAny<allstarr.Models.Domain.Song>(), It.IsAny<string>()), Times.Never);
            discovery.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(AppleDownloadEndpointState.Available, AppleDownloadCapabilityState.Available, true)]
    [InlineData(AppleDownloadEndpointState.Available, AppleDownloadCapabilityState.Unsupported, false)]
    [InlineData(AppleDownloadEndpointState.NeedsAuthentication, AppleDownloadCapabilityState.Degraded, false)]
    [InlineData(AppleDownloadEndpointState.Incompatible, AppleDownloadCapabilityState.Unsupported, false)]
    public async Task AvailabilityRequiresAcceptedDownloadContract(
        AppleDownloadEndpointState endpointState,
        AppleDownloadCapabilityState downloadState,
        bool expected)
    {
        var discovery = new Mock<IAppleDownloadEndpointDiscovery>(MockBehavior.Strict);
        discovery.Setup(item => item.DiscoverAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppleDownloadEndpointSnapshot(
                endpointState,
                null,
                "1.0.0",
                endpointState == AppleDownloadEndpointState.Available,
                [new AppleDownloadCapabilityStatus(ProviderCapabilities.Download, downloadState)]));

        var service = new AppleMusicDownloadService(
            new HttpFactory(),
            new ConfigurationBuilder().Build(),
            Mock.Of<ILocalLibraryService>(),
            Mock.Of<IMusicMetadataService>(),
            Options.Create(new SubsonicSettings()),
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-provider.lan" }),
            discovery.Object,
            Mock.Of<IServiceProvider>(),
            NullLogger<AppleMusicDownloadService>.Instance);

        Assert.Equal(expected, await service.IsAvailableAsync());
        discovery.VerifyAll();
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static AppleMusicDownloadService CreateService(
        string directory,
        ILocalLibraryService local,
        IMusicMetadataService metadata,
        IAppleDownloadEndpointDiscovery discovery,
        HttpMessageHandler handler) => new(
            new StubFactory(new HttpClient(handler)),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = directory
            }).Build(),
            local,
            metadata,
            Options.Create(new SubsonicSettings()),
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-provider.lan" }),
            discovery,
            Mock.Of<IServiceProvider>(),
            NullLogger<AppleMusicDownloadService>.Instance);

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }
}
