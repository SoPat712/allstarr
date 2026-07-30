using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace allstarr.Tests;

public class AuthHeaderHelperTests
{
    [Fact]
    public void ForwardAuthHeaders_ShouldPreferXEmbyAuthorization()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "MediaBrowser Token=\"abc\"",
            ["Authorization"] = "Bearer xyz"
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.True(request.Headers.TryGetValues("X-Emby-Authorization", out var values));
        Assert.Contains("MediaBrowser Token=\"abc\"", values);
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public void ForwardAuthHeaders_ShouldMapMediaBrowserAuthorizationToXEmby()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "MediaBrowser Client=\"Feishin\", Token=\"abc\""
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.True(request.Headers.Contains("X-Emby-Authorization"));
        Assert.True(request.Headers.TryGetValues("X-Emby-Token", out var tokens));
        Assert.Contains("abc", tokens);
    }

    [Fact]
    public void ExtractToken_ShouldReadMediaBrowserAuthorizationUsedByNativePlayers()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "MediaBrowser Client=\"Finer\", Device=\"iPhone\", Token=\"player-token\""
        };

        Assert.Equal("player-token", AuthHeaderHelper.ExtractToken(headers));
    }

    [Fact]
    public void ForwardAuthHeaders_ShouldForwardXEmbyToken()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Token"] = "abc"
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.True(request.Headers.TryGetValues("X-Emby-Token", out var values));
        Assert.Contains("abc", values);
    }

    [Fact]
    public void ForwardAuthHeaders_ShouldForwardStandardAuthorization()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "Bearer xyz"
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.True(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public void ExtractDeviceIdAndClientName_ShouldParseMediaBrowserHeader()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] =
                "MediaBrowser Client=\"Feishin\", Device=\"Desktop\", DeviceId=\"dev-123\", Version=\"1.0\", UserId=\"user-7\", Token=\"abc\""
        };

        Assert.Equal("dev-123", AuthHeaderHelper.ExtractDeviceId(headers));
        Assert.Equal("Feishin", AuthHeaderHelper.ExtractClientName(headers));
        Assert.Equal("user-7", AuthHeaderHelper.ExtractUserId(headers));
    }

    [Fact]
    public void CreateAuthHeader_ShouldBuildMediaBrowserString()
    {
        var header = AuthHeaderHelper.CreateAuthHeader(
            token: "abc",
            client: "Feishin",
            device: "Desktop",
            deviceId: "dev-123",
            version: "1.0");

        Assert.Contains("MediaBrowser", header);
        Assert.Contains("Client=\"Feishin\"", header);
        Assert.Contains("Device=\"Desktop\"", header);
        Assert.Contains("DeviceId=\"dev-123\"", header);
        Assert.Contains("Version=\"1.0\"", header);
        Assert.Contains("Token=\"abc\"", header);
    }
}
