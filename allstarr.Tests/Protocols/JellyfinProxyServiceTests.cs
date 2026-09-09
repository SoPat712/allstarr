using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
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
    private readonly IApplicationCache _cache;
    private readonly JellyfinSettings _settings;

    public JellyfinProxyServiceTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_mockHandler.Object);

        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);
        _cache = new DisabledApplicationCache();

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
            _cache,
            new MediaAssetResolver(
                _cache,
                new Mock<ILogger<MediaAssetResolver>>().Object),
            new ConfigurationBuilder().Build());
    }

    [Fact]
    public async Task GetJsonAsync_ValidResponse_ReturnsJsonDocument()
    {
        var jsonResponse = "{\"Items\":[{\"Id\":\"123\",\"Name\":\"Test Song\"}],\"TotalRecordCount\":1}";
        SetupMockResponse(HttpStatusCode.OK, jsonResponse, "application/json");

        var (body, statusCode) = await _service.GetJsonAsync("Items");

        Assert.NotNull(body);
        Assert.Equal(200, statusCode);
        Assert.True(body.RootElement.TryGetProperty("Items", out var items));
        Assert.Equal(1, items.GetArrayLength());
    }

    [Fact]
    public async Task GetJsonAsync_ServerError_ReturnsNull()
    {
        SetupMockResponse(HttpStatusCode.InternalServerError, "", "text/plain");

        var (body, statusCode) = await _service.GetJsonAsync("Items");

        Assert.Null(body);
        Assert.Equal(500, statusCode);
    }

    [Fact]
    public async Task PostJsonAsync_UpstreamFailure_DoesNotLogResponseBodyOrQuerySecret()
    {
        const string responseSecret = "upstream-session-secret";
        const string querySecret = "client-token-secret";
        SetupMockResponse(
            HttpStatusCode.InternalServerError,
            $$"""{"error":"{{responseSecret}}"}""",
            "application/json");

        var messages = new List<string>();
        var service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(_settings),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new CollectingLogger<JellyfinProxyService>(messages),
            _cache,
            new MediaAssetResolver(
                _cache,
                new Mock<ILogger<MediaAssetResolver>>().Object),
            new ConfigurationBuilder().Build());

        var (_, statusCode) = await service.PostJsonAsync(
            $"Items?api_key={querySecret}",
            "{}",
            new HeaderDictionary());

        Assert.Equal(500, statusCode);
        Assert.DoesNotContain(messages, message => message.Contains(responseSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains(querySecret, StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("<redacted>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetJsonAsync_WithoutClientHeaders_SendsNoAuth()
    {
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

        await _service.GetJsonAsync("Items");

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.Contains("Authorization"));
        Assert.False(captured.Headers.Contains("X-Emby-Authorization"));
    }

    [Fact]
    public async Task GetJsonAsync_WithXEmbyToken_UsesModernAuthorization()
    {
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

        await _service.GetJsonAsync("Items", null, headers);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("Authorization", out var values));
        Assert.Contains(values, value => value.Contains("Token=\"token-123\"", StringComparison.Ordinal));
        Assert.False(captured.Headers.Contains("X-Emby-Token"));
    }

    [Fact]
    public async Task GetBytesAsync_ReturnsBodyAndContentType()
    {
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

        var (body, contentType) = await _service.GetBytesAsync("Items/123/Images/Primary");

        Assert.Equal(imageBytes, body);
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public async Task GetBytesSafeAsync_OnError_ReturnsSuccessFalse()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var (body, contentType, success) = await _service.GetBytesSafeAsync("Items/123/Images/Primary");

        Assert.False(success);
        Assert.Null(body);
        Assert.Null(contentType);
    }

    [Fact]
    public async Task SearchAsync_BuildsCorrectQueryParams()
    {
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

        await _service.SearchAsync("test query", new[] { "Audio", "MusicAlbum" }, 25);

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
        HttpRequestMessage? captured = null;
        var itemJson = "{\"Items\":[{\"Id\":\"abc-123\",\"Name\":\"My Song\",\"Type\":\"Audio\"}],\"TotalRecordCount\":1}";
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => captured = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(itemJson)
            });

        var (body, statusCode) = await _service.GetItemAsync("abc-123");

        Assert.NotNull(captured);
        Assert.Contains("/Items?", captured!.RequestUri!.ToString());
        Assert.Contains("ids=abc-123", captured.RequestUri.ToString());
        Assert.Contains("limit=1", captured.RequestUri.ToString());
        Assert.Contains("userId=user-guid-here", captured.RequestUri.ToString());
        Assert.Contains("Token=\"test-api-key-12345\"", captured.Headers.GetValues("Authorization").Single());
        Assert.NotNull(body);
        Assert.Equal("abc-123", body.RootElement.GetProperty("Id").GetString());
        Assert.Equal(200, statusCode);
    }

    [Fact]
    public async Task GetItemAsync_WithClientTokenPreservesCallerScope()
    {
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"Items\":[{\"Id\":\"abc-123\",\"Type\":\"Audio\"}],\"TotalRecordCount\":1}")
            });
        var headers = new HeaderDictionary
        {
            ["X-Emby-Token"] = "caller-token"
        };

        var (body, statusCode) = await _service.GetItemAsync("abc-123", headers);

        Assert.Equal(200, statusCode);
        Assert.NotNull(body);
        Assert.NotNull(captured);
        Assert.DoesNotContain("userId=", captured!.RequestUri!.Query);
        Assert.Contains("Token=\"caller-token\"", captured.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task GetArtistAsync_WithClientTokenDoesNotInjectConfiguredUser()
    {
        HttpRequestMessage? captured = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Id\":\"00112233445566778899aabbccddeeff\",\"Type\":\"MusicArtist\"}")
            });
        var headers = new HeaderDictionary { ["X-Emby-Token"] = "caller-token" };

        await _service.GetArtistAsync("00112233445566778899aabbccddeeff", headers);

        Assert.NotNull(captured);
        Assert.DoesNotContain("userId=", captured!.RequestUri!.Query);
        Assert.Contains("Token=\"caller-token\"", captured.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task GetJsonAsync_WithEndpointQuery_PreservesCallerParameters()
    {
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

        await _service.GetJsonAsync(
            "Users/user-abc/Items/abc-123?api_key=query-token&Fields=DateCreated,PremiereDate,ProductionYear");

        Assert.NotNull(captured);
        var requestUri = captured!.RequestUri!;
        Assert.Contains("/Users/user-abc/Items/abc-123", requestUri.ToString());

        var query = System.Web.HttpUtility.ParseQueryString(requestUri.Query);
        Assert.Equal("query-token", query.Get("ApiKey"));
        Assert.DoesNotContain("api_key=", requestUri.Query, StringComparison.Ordinal);
        Assert.Equal("DateCreated,PremiereDate,ProductionYear", query.Get("Fields"));
    }

    [Fact]
    public async Task GetJsonAsync_WithRepeatedFields_PreservesAllFieldParameters()
    {
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

        await _service.GetJsonAsync(
            "Playlists/playlist-123/Items?Fields=Genres&Fields=DateCreated&Fields=MediaSources&UserId=user-abc");

        Assert.NotNull(captured);
        var query = captured!.RequestUri!.Query;
        Assert.Contains("Fields=Genres", query);
        Assert.Contains("Fields=DateCreated", query);
        Assert.Contains("Fields=MediaSources", query);
        Assert.Contains("UserId=user-abc", query);
    }

    [Fact]
    public async Task SendAsync_WithNoBody_PreservesEmptyRequestBody()
    {
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

        var (_, statusCode) = await _service.SendAsync(
            HttpMethod.Post,
            "Sessions/session-123/Playing/Pause?controllingUserId=user-123",
            null,
            headers);

        Assert.Equal(204, statusCode);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Null(captured.Content);
    }

    [Fact]
    public async Task SendAsync_WithCustomContentType_PreservesOriginalType()
    {
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

        await _service.SendAsync(
            HttpMethod.Put,
            "Sessions/session-123/Command/DisplayMessage",
            "{\"Text\":\"hello\"}",
            headers,
            "application/json; charset=utf-8");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.NotNull(captured.Content);
        Assert.Equal("application/json; charset=utf-8", captured.Content!.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task GetJsonAsync_WithEndpointAndExplicitQuery_MergesWithExplicitPrecedence()
    {
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

        await _service.GetJsonAsync(
            "Items/abc-123?api_key=endpoint-token&Fields=DateCreated",
            new Dictionary<string, string>
            {
                ["api_key"] = "explicit-token",
                ["UserId"] = "route-user"
            });

        Assert.NotNull(captured);
        var query = System.Web.HttpUtility.ParseQueryString(captured!.RequestUri!.Query);
        Assert.Equal("explicit-token", query.Get("ApiKey"));
        Assert.DoesNotContain("api_key=", captured.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("DateCreated", query.Get("Fields"));
        Assert.Equal("route-user", query.Get("UserId"));
    }

    [Fact]
    public async Task GetArtistsAsync_WithSearchTerm_IncludesInQuery()
    {
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

        await _service.GetArtistsAsync("Beatles", 10);

        Assert.NotNull(captured);
        var url = captured!.RequestUri!.ToString();
        Assert.Contains("/Artists", url);
        Assert.Contains("searchTerm=Beatles", url);
        Assert.Contains("limit=10", url);
    }

    [Fact]
    public async Task GetImageAsync_WithDimensions_IncludesMaxWidthHeight()
    {
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

        await _service.GetImageAsync("item-123", "Primary", maxWidth: 300, maxHeight: 300);

        Assert.NotNull(captured);
        var url = captured!.RequestUri!.ToString();
        Assert.Contains("/Items/item-123/Images/Primary", url);
        Assert.Contains("maxWidth=300", url);
        Assert.Contains("maxHeight=300", url);
    }

    [Fact]
    public async Task GetImageAsync_WithTag_IncludesTagInQuery()
    {
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

        await _service.GetImageAsync("item-123", "Primary", imageTag: "playlist-art-v2");

        Assert.NotNull(captured);
        var query = System.Web.HttpUtility.ParseQueryString(captured!.RequestUri!.Query);
        Assert.Equal("playlist-art-v2", query.Get("tag"));
    }



    [Fact]
    public async Task TestConnectionAsync_ValidServer_ReturnsSuccess()
    {
        var serverInfo = "{\"ServerName\":\"My Jellyfin\",\"Version\":\"10.8.0\"}";
        SetupMockResponse(HttpStatusCode.OK, serverInfo, "application/json");

        var (success, serverName, version) = await _service.TestConnectionAsync();

        Assert.True(success);
        Assert.Equal("My Jellyfin", serverName);
        Assert.Equal("10.8.0", version);
    }

    [Fact]
    public async Task TestConnectionAsync_ServerDown_ReturnsFalse()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var (success, serverName, version) = await _service.TestConnectionAsync();

        Assert.False(success);
        Assert.Null(serverName);
        Assert.Null(version);
    }



    [Fact]
    public async Task StreamAudioAsync_NullContext_ReturnsError()
    {
        var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
        var mockLogger = new Mock<ILogger<JellyfinProxyService>>();
        var cache = new DisabledApplicationCache();

        var service = new JellyfinProxyService(
            _mockHttpClientFactory.Object,
            Options.Create(_settings),
            httpContextAccessor,
            mockLogger.Object,
            cache,
            new MediaAssetResolver(
                cache,
                new Mock<ILogger<MediaAssetResolver>>().Object),
            new ConfigurationBuilder().Build());

        var result = await service.StreamAudioAsync("song-123", CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task SendPassthroughResponseAsync_PreservesMethodQueryBodyAndConditionalHeaders()
    {
        string? observedMethod = null;
        string? observedPathAndQuery = null;
        string? observedBody = null;
        string? observedContentType = null;
        string[] observedIfMatch = [];
        string[] observedAuth = [];
        string[] observedClientHeader = [];
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                observedMethod = request.Method.Method;
                observedPathAndQuery = request.RequestUri!.PathAndQuery;
                observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                observedContentType = request.Content.Headers.ContentType?.ToString();
                observedIfMatch = request.Headers.GetValues("If-Match").ToArray();
                observedAuth = request.Headers.GetValues("Authorization").ToArray();
                observedClientHeader = request.Headers.GetValues("X-Jellyfin-Client-Capability").ToArray();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("accepted", System.Text.Encoding.UTF8, "text/plain")
            });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Patch;
        context.Request.ContentType = "application/merge-patch+json";
        context.Request.Headers.Append("If-Match", "\"one\"");
        context.Request.Headers.Append("If-Match", "\"two\"");
        context.Request.Headers["X-Emby-Authorization"] = "MediaBrowser Token=\"client-token\"";
        context.Request.Headers["X-Jellyfin-Client-Capability"] = "gapless";
        var payload = System.Text.Encoding.UTF8.GetBytes("{\"Name\":\"Updated\"}");
        context.Request.ContentLength = payload.Length;
        context.Request.Body = new MemoryStream(payload);

        using var response = await _service.SendPassthroughResponseAsync(
            context.Request,
            "Items/item-1?api_key=client-token&Fields=Name");

        Assert.Equal("PATCH", observedMethod);
        Assert.Equal("/Items/item-1?ApiKey=client-token&Fields=Name", observedPathAndQuery);
        Assert.Equal("{\"Name\":\"Updated\"}", observedBody);
        Assert.Equal("application/merge-patch+json", observedContentType);
        Assert.Equal(["\"one\"", "\"two\""], observedIfMatch);
        Assert.Equal(["MediaBrowser Token=\"client-token\""], observedAuth);
        Assert.Equal(["gapless"], observedClientHeader);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("http://jellyfin/Items/1?api_key=legacy&Fields=Name", "http://jellyfin/Items/1?ApiKey=legacy&Fields=Name")]
    [InlineData("http://jellyfin/Items/1?access_token=legacy", "http://jellyfin/Items/1?ApiKey=legacy")]
    [InlineData("http://jellyfin/Items/1?api_key=old&ApiKey=modern", "http://jellyfin/Items/1?ApiKey=modern")]
    public void NormalizeQueryCredentials_UpgradesLegacyJellyfinTokens(string input, string expected)
    {
        Assert.Equal(expected, JellyfinProxyService.NormalizeQueryCredentials(input));
    }

    [Fact]
    public async Task SendPassthroughResponseAsync_ForwardsDetectedBodyWithoutLength()
    {
        string? observedBody = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));
        var context = new DefaultHttpContext();
        var bodyFeature = new Mock<IHttpRequestBodyDetectionFeature>();
        bodyFeature.SetupGet(feature => feature.CanHaveBody).Returns(true);
        context.Features.Set(bodyFeature.Object);
        context.Request.Method = HttpMethods.Post;
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"Name":"HTTP/2"}"""));

        using var response = await _service.SendPassthroughResponseAsync(
            context.Request, "Playlists/playlist-1");

        Assert.Equal("""{"Name":"HTTP/2"}""", observedBody);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task SendPassthroughResponseAsync_PreservesEmptyBodyWithoutLength()
    {
        var observedContent = false;
        long? observedContentLength = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                observedContent = request.Content != null;
                observedContentLength = request.Content?.Headers.ContentLength;
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;

        using var response = await _service.SendPassthroughResponseAsync(
            context.Request, "Playlists/playlist-1/Items/item-1/Move/0");

        Assert.True(observedContent);
        Assert.Equal(0, observedContentLength);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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

    private sealed class CollectingLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
        }
    }
}
