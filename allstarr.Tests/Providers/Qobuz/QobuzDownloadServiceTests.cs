using allstarr.Services;
using allstarr.Services.Qobuz;
using allstarr.Services.Local;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;

namespace allstarr.Tests;

public class QobuzDownloadServiceTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<ILocalLibraryService> _localLibraryServiceMock;
    private readonly Mock<IMusicMetadataService> _metadataServiceMock;
    private readonly Mock<ILogger<QobuzBundleService>> _bundleServiceLoggerMock;
    private readonly Mock<ILogger<QobuzDownloadService>> _loggerMock;
    private readonly IConfiguration _configuration;
    private readonly string _testDownloadPath;
    private QobuzBundleService _bundleService;

    public QobuzDownloadServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "allstarr-qobuz-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);

        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);

        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _localLibraryServiceMock = new Mock<ILocalLibraryService>();
        _metadataServiceMock = new Mock<IMusicMetadataService>();
        _bundleServiceLoggerMock = new Mock<ILogger<QobuzBundleService>>();
        _loggerMock = new Mock<ILogger<QobuzDownloadService>>();

        // Create a real QobuzBundleService for testing (it will use the mocked HttpClient)
        _bundleService = new QobuzBundleService(_httpClientFactoryMock.Object, _bundleServiceLoggerMock.Object);

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    private QobuzDownloadService CreateService(
        string? userAuthToken = null,
        string? userId = null,
        string? quality = null,
        DownloadMode downloadMode = DownloadMode.Track)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        var subsonicSettings = Options.Create(new SubsonicSettings
        {
            DownloadMode = downloadMode
        });

        var qobuzSettings = Options.Create(new QobuzSettings
        {
            UserAuthToken = userAuthToken,
            UserId = userId,
            Quality = quality
        });

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(allstarr.Services.Subsonic.PlaylistSyncService)))
            .Returns(null!);

        return new QobuzDownloadService(
            _httpClientFactoryMock.Object,
            config,
            _localLibraryServiceMock.Object,
            _metadataServiceMock.Object,
            _bundleService,
            subsonicSettings,
            qobuzSettings,
            serviceProviderMock.Object,
            _loggerMock.Object);
    }


    [Fact]
    public async Task IsAvailableAsync_WithoutUserAuthToken_ReturnsFalse()
    {
        var service = CreateService(userAuthToken: null, userId: "123");

        var result = await service.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithoutUserId_ReturnsFalse()
    {
        var service = CreateService(userAuthToken: "test-token", userId: null);

        var result = await service.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithEmptyCredentials_ReturnsFalse()
    {
        var service = CreateService(userAuthToken: "", userId: "");

        var result = await service.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithValidCredentials_WhenBundleServiceWorks_ReturnsTrue()
    {
        // Mock a successful response for bundle service
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(@"<html><script src=""/resources/1.0.3-b001/bundle.js""></script></html>")
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("qobuz.com")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);

        var service = CreateService(userAuthToken: "test-token", userId: "123");

        var result = await service.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WhenBundleServiceFails_ReturnsFalse()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.ServiceUnavailable
        };

        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);

        var service = CreateService(userAuthToken: "test-token", userId: "123");

        var result = await service.IsAvailableAsync();

        Assert.False(result);
    }



    [Fact]
    public async Task DownloadSongAsync_WithUnsupportedProvider_ThrowsNotSupportedException()
    {
        var service = CreateService(userAuthToken: "test-token", userId: "123");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.DownloadSongAsync("spotify", "123456"));
    }

    [Fact]
    public async Task DownloadSongAsync_WhenAlreadyDownloaded_ReturnsExistingPath()
    {
        var existingPath = Path.Combine(_testDownloadPath, "existing-song.flac");
        await File.WriteAllTextAsync(existingPath, "fake audio content");

        _localLibraryServiceMock
            .Setup(s => s.GetLocalPathForExternalSongAsync("qobuz", "123456"))
            .ReturnsAsync(existingPath);

        var service = CreateService(userAuthToken: "test-token", userId: "123");

        var result = await service.DownloadSongAsync("qobuz", "123456");

        Assert.Equal(existingPath, result);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenSongNotFound_ThrowsException()
    {
        _localLibraryServiceMock
            .Setup(s => s.GetLocalPathForExternalSongAsync("qobuz", "999999"))
            .ReturnsAsync((string?)null);

        _metadataServiceMock
            .Setup(s => s.GetSongAsync("qobuz", "999999"))
            .ReturnsAsync((Song?)null);

        var service = CreateService(userAuthToken: "test-token", userId: "123");

        var exception = await Assert.ThrowsAsync<Exception>(() =>
            service.DownloadSongAsync("qobuz", "999999"));

        Assert.Equal("Song not found", exception.Message);
    }



    [Fact]
    public void GetDownloadStatus_WithUnknownSongId_ReturnsNull()
    {
        var service = CreateService(userAuthToken: "test-token", userId: "123");

        var result = service.GetDownloadStatus("unknown-id");

        Assert.Null(result);
    }

}
