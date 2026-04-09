using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using System.Net;
using System.Text.Json;

namespace allstarr.Tests;

public class JellyfinProxyServiceTests
{
    private readonly JellyfinProxyService _service;
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly RedisCacheService _cache;
    private readonly JellyfinSettings _settings;

    public JellyfinProxyServiceTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_mockHandler.Object);

        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var redisSettings = new RedisSettings { Enabled = false };
        var mockCacheLogger = new Mock<ILogger<RedisCacheService>>();
        _cache = new RedisCacheService(Options.Create(redisSettings), mockCacheLogger.Object);

        _settings = new JellyfinSettings
        {
            Url = "http://localhost:8096",
            ApiKey = "test-api-key-12345",
            UserId = "user-guid-here",
            ClientName = "TestClient",
            DeviceName = "TestDevice",
            DeviceId = "test-device-id",
            ClientVersion = "1.0.3"
        };

        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();

        // Initialize cache settings for tests
        var serviceCollection = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        serviceCollection.Configure<CacheSettings>(options => { }); // Use defaults
        var serviceProvider = serviceCollection.BuildServiceProvider();
        CacheExtensions.InitializeCacheSettings(serviceProvider);

        _service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(_settings),
            httpContextAccessor,
            mockLogger.Object,
            _cache);
    }

    [Fact]
    public async Task GetJsonAsync_ValidResponse_ReturnsJsonDocument()
    {
        // Arrange
        var jsonResponse = "{\"Items\":[{\"Id\":\"123\",\"Name\":\"Test Song\"}],\"TotalRecordCount\":1}";
        SetupMockResponse(HttpStatusCode.OK, jsonResponse, "application/json");

        // Act
        var (body, statusCode) = await _service.GetJsonAsync("Items");

        // Assert
        Assert.NotNull(body);
        Assert.Equal(200, statusCode);
        Assert.True(body.RootElement.TryGetProperty("Items", out var items));
        Assert.Equal(1, items.GetArrayLength());
    }

    [Fact]
    public async Task GetJsonAsync_ServerError_ReturnsNull()
    {
        // Arrange
        SetupMockResponse(HttpStatusCode.InternalServerError, "", "text/plain");

        // Act
        var (body, statusCode) = await _service.GetJsonAsync("Items");

        // Assert
        Assert.Null(body);
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task GetJsonAsync_WithoutClientHeaders_SendsNoAuth()
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

        // Assert - Should NOT include auth when no client headers provided
        Assert.NotNull(captured);
        Assert.False(captured!.Headers.Contains("Authorization"));
        Assert.False(captured.Headers.Contains("X-Emby-Authorization"));
    }

    [Fact]
    public async Task GetJsonAsync_WithXEmbyToken_ForwardsTokenHeader()
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

        var headers = new HeaderDictionary
        {
            ["X-Emby-Token"] = "token-123"
        };

        // Act
        await _service.GetJsonAsync("Items", null, headers);

        // Assert
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("X-Emby-Token", out var values));
        Assert.Contains("token-123", values);
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

        // Verify the query parameters are properly URL encoded
        Assert.Contains("searchTerm=", url);
        Assert.Contains("test", url);
        Assert.Contains("query", url);
        Assert.Contains("includeItemTypes=", url);
        Assert.Contains("Audio", url);
        Assert.Contains("MusicAlbum", url);
        Assert.Contains("limit=25", url);
        Assert.Contains("recursive=true", url);

        // Verify spaces are encoded (either as %20 or +)
        var uri = captured.RequestUri;
        var searchTermValue = System.Web.HttpUtility.ParseQueryString(uri!.Query).Get("searchTerm");
        Assert.Equal("test query", searchTermValue);
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
        var (body, statusCode) = await _service.GetItemAsync("abc-123");

        // Assert
        Assert.NotNull(captured);
        Assert.Contains("/Items/abc-123", captured!.RequestUri!.ToString());
        Assert.NotNull(body);
        Assert.Equal(200, statusCode);
    }

    [Fact]
    public async Task GetJsonAsync_WithEndpointQuery_PreservesCallerParameters()
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
                Content = new StringContent("{\"Id\":\"abc-123\"}")
            });

        // Act
        await _service.GetJsonAsync(
            "Users/user-abc/Items/abc-123?api_key=query-token&Fields=DateCreated,PremiereDate,ProductionYear");

        // Assert
        Assert.NotNull(captured);
        var requestUri = captured!.RequestUri!;
        Assert.Contains("/Users/user-abc/Items/abc-123", requestUri.ToString());

        var query = System.Web.HttpUtility.ParseQueryString(requestUri.Query);
        Assert.Equal("query-token", query.Get("api_key"));
        Assert.Equal("DateCreated,PremiereDate,ProductionYear", query.Get("Fields"));
    }

    [Fact]
    public async Task GetJsonAsync_WithRepeatedFields_PreservesAllFieldParameters()
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
                Content = new StringContent("{\"Items\":[]}")
            });

        // Act
        await _service.GetJsonAsync(
            "Playlists/playlist-123/Items?Fields=Genres&Fields=DateCreated&Fields=MediaSources&UserId=user-abc");

        // Assert
        Assert.NotNull(captured);
        var query = captured!.RequestUri!.Query;
        Assert.Contains("Fields=Genres", query);
        Assert.Contains("Fields=DateCreated", query);
        Assert.Contains("Fields=MediaSources", query);
        Assert.Contains("UserId=user-abc", query);
    }

    [Fact]
    public async Task GetPassthroughResponseAsync_WithRepeatedFields_PreservesAllFieldParameters()
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
                Content = new StringContent("{\"Items\":[]}")
            });

        // Act
        var response = await _service.GetPassthroughResponseAsync(
            "Playlists/playlist-123/Items?Fields=Genres&Fields=DateCreated&Fields=MediaSources&UserId=user-abc");

        // Assert
        Assert.NotNull(captured);
        var query = captured!.RequestUri!.Query;
        Assert.Contains("Fields=Genres", query);
        Assert.Contains("Fields=DateCreated", query);
        Assert.Contains("Fields=MediaSources", query);
        Assert.Contains("UserId=user-abc", query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPassthroughResponseAsync_WithClientAuth_ForwardsAuthHeader()
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
                Content = new StringContent("{\"Items\":[]}")
            });

        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "MediaBrowser Token=\"abc\""
        };

        // Act
        var response = await _service.GetPassthroughResponseAsync(
            "Playlists/playlist-123/Items?Fields=Genres",
            headers);

        // Assert
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("X-Emby-Authorization", out var values));
        Assert.Contains("MediaBrowser Token=\"abc\"", values);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_WithNoBody_PreservesEmptyRequestBody()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));

        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "MediaBrowser Token=\"abc\""
        };

        // Act
        var (_, statusCode) = await _service.SendAsync(
            HttpMethod.Post,
            "Sessions/session-123/Playing/Pause?controllingUserId=user-123",
            null,
            headers);

        // Assert
        Assert.Equal(204, statusCode);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Null(captured.Content);
    }

    [Fact]
    public async Task SendAsync_WithCustomContentType_PreservesOriginalType()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));

        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "MediaBrowser Token=\"abc\""
        };

        // Act
        await _service.SendAsync(
            HttpMethod.Put,
            "Sessions/session-123/Command/DisplayMessage",
            "{\"Text\":\"hello\"}",
            headers,
            "application/json; charset=utf-8");

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.NotNull(captured.Content);
        Assert.Equal("application/json; charset=utf-8", captured.Content!.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task GetPassthroughResponseAsync_WithAcceptEncoding_ForwardsCompressionHeaders()
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
                Content = new StringContent("{\"Items\":[]}")
            });

        var headers = new HeaderDictionary
        {
            ["Accept-Encoding"] = "gzip, br",
            ["User-Agent"] = "Finamp/1.0",
            ["Accept-Language"] = "en-US"
        };

        // Act
        var response = await _service.GetPassthroughResponseAsync(
            "Playlists/playlist-123/Items?Fields=Genres",
            headers);

        // Assert
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("Accept-Encoding", out var encodings));
        Assert.Contains("gzip", encodings);
        Assert.Contains("br", encodings);
        Assert.True(captured.Headers.TryGetValues("User-Agent", out var userAgents));
        Assert.Contains("Finamp/1.0", userAgents);
        Assert.True(captured.Headers.TryGetValues("Accept-Language", out var languages));
        Assert.Contains("en-US", languages);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetJsonAsync_WithEndpointAndExplicitQuery_MergesWithExplicitPrecedence()
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
                Content = new StringContent("{\"Items\":[]}")
            });

        // Act
        await _service.GetJsonAsync(
            "Items/abc-123?api_key=endpoint-token&Fields=DateCreated",
            new Dictionary<string, string>
            {
                ["api_key"] = "explicit-token",
                ["UserId"] = "route-user"
            });

        // Assert
        Assert.NotNull(captured);
        var query = System.Web.HttpUtility.ParseQueryString(captured!.RequestUri!.Query);
        Assert.Equal("explicit-token", query.Get("api_key"));
        Assert.Equal("DateCreated", query.Get("Fields"));
        Assert.Equal("route-user", query.Get("UserId"));
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
    public async Task GetImageAsync_WithTag_IncludesTagInQuery()
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
        await _service.GetImageAsync("item-123", "Primary", imageTag: "playlist-art-v2");

        // Assert
        Assert.NotNull(captured);
        var query = System.Web.HttpUtility.ParseQueryString(captured!.RequestUri!.Query);
        Assert.Equal("playlist-art-v2", query.Get("tag"));
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
    public async Task StreamAudioAsync_NullContext_ReturnsError()
    {
        // Arrange
        var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();
        var redisSettings = new RedisSettings { Enabled = false };
        var mockCacheLogger = new Mock<ILogger<RedisCacheService>>();
        var cache = new RedisCacheService(Options.Create(redisSettings), mockCacheLogger.Object);

        var service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(_settings),
            httpContextAccessor,
            mockLogger.Object,
            cache);

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
