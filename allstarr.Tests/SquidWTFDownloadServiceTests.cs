using System.Net;
using System.Reflection;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Services.SquidWTF;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public class SquidWTFDownloadServiceTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
    private readonly Mock<ILocalLibraryService> _localLibraryServiceMock = new();
    private readonly Mock<IMusicMetadataService> _metadataServiceMock = new();
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<ILogger<SquidWTFDownloadService>> _loggerMock = new();
    private readonly Mock<ILogger<OdesliService>> _odesliLoggerMock = new();
    private readonly Mock<ILogger<RedisCacheService>> _redisLoggerMock = new();
    private readonly string _testDownloadPath;
    private readonly List<string> _apiUrls =
    [
        "http://127.0.0.1:18081",
        "http://127.0.0.1:18082"
    ];

    public SquidWTFDownloadServiceTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "allstarr-squidwtf-download-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);

        _serviceProviderMock
            .Setup(sp => sp.GetService(typeof(allstarr.Services.Subsonic.PlaylistSyncService)))
            .Returns((object?)null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    [Fact]
    public void BuildQualityFallbackOrder_MapsConfiguredQualityToDescendingFallbacks()
    {
        var order = InvokePrivateStaticMethod<IReadOnlyList<string>>(
            typeof(SquidWTFDownloadService),
            "BuildQualityFallbackOrder",
            "HI_RES");

        Assert.Equal(["HI_RES_LOSSLESS", "LOSSLESS", "HIGH", "LOW"], order);
    }



    private SquidWTFDownloadService CreateService(HttpMessageHandler handler, string quality)
    {
        var httpClient = new HttpClient(handler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();

        var subsonicSettings = Options.Create(new SubsonicSettings
        {
            DownloadMode = DownloadMode.Track,
            StorageMode = StorageMode.Cache
        });

        var squidwtfSettings = Options.Create(new SquidWTFSettings
        {
            Quality = quality
        });

        var cache = new RedisCacheService(
            Options.Create(new RedisSettings { Enabled = false }),
            _redisLoggerMock.Object);

        var odesliService = new OdesliService(_httpClientFactoryMock.Object, _odesliLoggerMock.Object, cache);

        return new SquidWTFDownloadService(
            _httpClientFactoryMock.Object,
            configuration,
            _localLibraryServiceMock.Object,
            _metadataServiceMock.Object,
            subsonicSettings,
            squidwtfSettings,
            _serviceProviderMock.Object,
            _loggerMock.Object,
            odesliService,
            _apiUrls);
    }

    private static string CreateTrackResponseJson(string audioQuality, string mimeType, string downloadUrl)
    {
        var manifestJson = $$"""
        {
          "mimeType": "{{mimeType}}",
          "codecs": "aac",
          "encryptionType": "NONE",
          "urls": ["{{downloadUrl}}"]
        }
        """;

        var manifestBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestJson));

        return $$"""
        {
          "version": "2.4",
          "data": {
            "audioQuality": "{{audioQuality}}",
            "manifest": "{{manifestBase64}}"
          }
        }
        """;
    }

    private static async Task<object> InvokePrivateAsync(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(target, parameters) as Task;
        Assert.NotNull(task);

        await task!;

        var resultProperty = task.GetType().GetProperty("Result");
        Assert.NotNull(resultProperty);

        var result = resultProperty!.GetValue(task);
        Assert.NotNull(result);
        return result!;
    }

    private static T InvokePrivateStaticMethod<T>(Type targetType, string methodName, params object?[] parameters)
    {
        var method = targetType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, parameters);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static T GetProperty<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);

        var value = property!.GetValue(target);
        Assert.NotNull(value);
        return (T)value!;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
