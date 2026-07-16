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
}
