using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;
using System.Net;
using System.Text.Json;

namespace allstarr.Tests;

public class JellyfinProxyServiceTests
{
    private readonly JellyfinProxyService _service;
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly JellyfinSettings _settings;

    public JellyfinProxyServiceTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_mockHandler.Object);

        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _settings = new JellyfinSettings
        {
            Url = "http://localhost:8096",
            ApiKey = "test-api-key-12345",
            UserId = "user-guid-here",
            ClientName = "TestClient",
            DeviceName = "TestDevice",
            DeviceId = "test-device-id",
            ClientVersion = "1.0.0"
        };

        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();

        _service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(_settings),
            httpContextAccessor,
            mockLogger.Object);
    }

    [Fact]
    public async Task GetJsonAsync_ValidResponse_ReturnsJsonDocument()
    {
        // Arrange
        var jsonResponse = "{\"Items\":[{\"Id\":\"123\",\"Name\":\"Test Song\"}],\"TotalRecordCount\":1}";
        SetupMockResponse(HttpStatusCode.OK, jsonResponse, "application/json");

        // Act
        var result = await _service.GetJsonAsync("Items");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.RootElement.TryGetProperty("Items", out var items));
        Assert.Equal(1, items.GetArrayLength());
    }

    [Fact]
    public async Task GetJsonAsync_ServerError_ReturnsNull()
    {
        // Arrange
        SetupMockResponse(HttpStatusCode.InternalServerError, "", "text/plain");

        // Act
        var result = await _service.GetJsonAsync("Items");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetJsonAsync_IncludesAuthHeader()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });

        // Act
        await _service.GetJsonAsync("Items");

        // Assert
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.Contains("Authorization"));
        var authHeader = captured.Headers.GetValues("Authorization").First();
        Assert.Contains("MediaBrowser", authHeader);
        Assert.Contains(_settings.ApiKey, authHeader);
        Assert.Contains(_settings.ClientName, authHeader);
    }

    [Fact]
    public async Task GetBytesAsync_ReturnsBodyAndContentType()
    {
        // Arrange
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(imageBytes)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act
        var (body, contentType) = await _service.GetBytesAsync("Items/123/Images/Primary");

        // Assert
        Assert.Equal(imageBytes, body);
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public async Task GetBytesSafeAsync_OnError_ReturnsSuccessFalse()
    {
        // Arrange
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Act
        var (body, contentType, success) = await _service.GetBytesSafeAsync("Items/123/Images/Primary");

        // Assert
        Assert.False(success);
        Assert.Null(body);
        Assert.Null(contentType);
    }

    [Fact]
    public async Task SearchAsync_BuildsCorrectQueryParams()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Items\":[],\"TotalRecordCount\":0}")
            });

        // Act
        await _service.SearchAsync("test query", new[] { "Audio", "MusicAlbum" }, 25);

        // Assert
        Assert.NotNull(captured);
        var url = captured!.RequestUri!.ToString();
        Assert.Contains("searchTerm=test%20query", url);
        Assert.Contains("includeItemTypes=Audio%2CMusicAlbum", url);
        Assert.Contains("limit=25", url);
        Assert.Contains("recursive=true", url);
    }

    [Fact]
    public async Task GetItemAsync_RequestsCorrectEndpoint()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        var itemJson = "{\"Id\":\"abc-123\",\"Name\":\"My Song\",\"Type\":\"Audio\"}";
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(itemJson)
            });

        // Act
        var result = await _service.GetItemAsync("abc-123");

        // Assert
        Assert.NotNull(captured);
        Assert.Contains("/Items/abc-123", captured!.RequestUri!.ToString());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetArtistsAsync_WithSearchTerm_IncludesInQuery()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Items\":[],\"TotalRecordCount\":0}")
            });

        // Act
        await _service.GetArtistsAsync("Beatles", 10);

        // Assert
        Assert.NotNull(captured);
        var url = captured!.RequestUri!.ToString();
        Assert.Contains("/Artists", url);
        Assert.Contains("searchTerm=Beatles", url);
        Assert.Contains("limit=10", url);
    }

    [Fact]
    public async Task GetImageAsync_WithDimensions_IncludesMaxWidthHeight()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            });

        // Act
        await _service.GetImageAsync("item-123", "Primary", maxWidth: 300, maxHeight: 300);

        // Assert
        Assert.NotNull(captured);
        var url = captured!.RequestUri!.ToString();
        Assert.Contains("/Items/item-123/Images/Primary", url);
        Assert.Contains("maxWidth=300", url);
        Assert.Contains("maxHeight=300", url);
    }

    [Fact]
    public async Task MarkFavoriteAsync_PostsToCorrectEndpoint()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await _service.MarkFavoriteAsync("song-456");

        // Assert
        Assert.True(result);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains($"/Users/{_settings.UserId}/FavoriteItems/song-456", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task MarkFavoriteAsync_WithoutUserId_ReturnsFalse()
    {
        // Arrange - create service without UserId
        var settingsWithoutUser = new JellyfinSettings
        {
            Url = "http://localhost:8096",
            ApiKey = "test-key",
            UserId = "" // no user
        };

        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();

        var service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(settingsWithoutUser),
            httpContextAccessor,
            mockLogger.Object);

        // Act
        var result = await service.MarkFavoriteAsync("song-456");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task TestConnectionAsync_ValidServer_ReturnsSuccess()
    {
        // Arrange
        var serverInfo = "{\"ServerName\":\"My Jellyfin\",\"Version\":\"10.8.0\"}";
        SetupMockResponse(HttpStatusCode.OK, serverInfo, "application/json");

        // Act
        var (success, serverName, version) = await _service.TestConnectionAsync();

        // Assert
        Assert.True(success);
        Assert.Equal("My Jellyfin", serverName);
        Assert.Equal("10.8.0", version);
    }

    [Fact]
    public async Task TestConnectionAsync_ServerDown_ReturnsFalse()
    {
        // Arrange
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        // Act
        var (success, serverName, version) = await _service.TestConnectionAsync();

        // Assert
        Assert.False(success);
        Assert.Null(serverName);
        Assert.Null(version);
    }

    [Fact]
    public async Task GetMusicLibraryIdAsync_WhenConfigured_ReturnsConfiguredId()
    {
        // Arrange - settings already have LibraryId set
        var settingsWithLibrary = new JellyfinSettings
        {
            Url = "http://localhost:8096",
            ApiKey = "test-key",
            LibraryId = "configured-library-id"
        };

        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();

        var service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(settingsWithLibrary),
            httpContextAccessor,
            mockLogger.Object);

        // Act
        var result = await service.GetMusicLibraryIdAsync();

        // Assert
        Assert.Equal("configured-library-id", result);
    }

    [Fact]
    public async Task GetMusicLibraryIdAsync_AutoDetects_MusicLibrary()
    {
        // Arrange
        var librariesJson = "{\"Items\":[{\"Id\":\"video-lib\",\"CollectionType\":\"movies\"},{\"Id\":\"music-lib-123\",\"CollectionType\":\"music\"}]}";
        SetupMockResponse(HttpStatusCode.OK, librariesJson, "application/json");

        var settingsNoLibrary = new JellyfinSettings
        {
            Url = "http://localhost:8096",
            ApiKey = "test-key",
            LibraryId = "" // not configured
        };

        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();

        var service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(settingsNoLibrary),
            httpContextAccessor,
            mockLogger.Object);

        // Act
        var result = await service.GetMusicLibraryIdAsync();

        // Assert
        Assert.Equal("music-lib-123", result);
    }

    [Fact]
    public async Task StreamAudioAsync_NullContext_ReturnsError()
    {
        // Arrange
        var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();

        var service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(_settings),
            httpContextAccessor,
            mockLogger.Object);

        // Act
        var result = await service.StreamAudioAsync("song-123", CancellationToken.None);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    private void SetupMockResponse(HttpStatusCode statusCode, string content, string contentType)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);
    }
}
