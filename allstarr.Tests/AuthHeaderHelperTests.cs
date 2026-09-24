using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace allstarr.Tests;

public class AuthHeaderHelperTests
{
    [Fact]
    public void ForwardAuthHeaders_ShouldPreferStandardAuthorization()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "MediaBrowser Token=\"old\"",
            ["X-Emby-Token"] = "old",
            ["Authorization"] = "MediaBrowser Client=\"Yuzic\", Device=\"Phone\", DeviceId=\"device-1\", Version=\"2.0\""
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.True(request.Headers.TryGetValues("Authorization", out var values));
        Assert.Contains("MediaBrowser Client=\"Yuzic\", Device=\"Phone\", DeviceId=\"device-1\", Version=\"2.0\"", values);
        Assert.False(request.Headers.Contains("X-Emby-Authorization"));
        Assert.False(request.Headers.Contains("X-Emby-Token"));
    }

    [Fact]
    public void ForwardAuthHeaders_ShouldPreserveMediaBrowserAuthorization()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "MediaBrowser Client=\"Feishin\", Token=\"abc\""
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.Equal(headers["Authorization"].ToString(), request.Headers.GetValues("Authorization").Single());
        Assert.False(request.Headers.Contains("X-Emby-Authorization"));
    }

    [Fact]
    public void ForwardAuthHeaders_ShouldUpgradeLegacyMediaBrowserHeader()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "MediaBrowser Client=\"OlderClient\", Device=\"Phone\", DeviceId=\"device-2\", Version=\"1.0\""
        };

        using var request = new HttpRequestMessage();
        Assert.True(AuthHeaderHelper.ForwardAuthHeaders(headers, request));
        Assert.Equal(headers["X-Emby-Authorization"].ToString(), request.Headers.GetValues("Authorization").Single());
        Assert.False(request.Headers.Contains("X-Emby-Authorization"));
    }

    [Fact]
    public void ForwardAuthHeaders_ShouldKeepLegacyEmbySchemeForOlderServers()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "Emby Client=\"OlderClient\""
        };

        using var request = new HttpRequestMessage();
        Assert.True(AuthHeaderHelper.ForwardAuthHeaders(headers, request));
        Assert.Equal(headers["X-Emby-Authorization"].ToString(), request.Headers.GetValues("X-Emby-Authorization").Single());
        Assert.False(request.Headers.Contains("Authorization"));
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
                "MediaBrowser Client=\"Feishin\", Device=\"Desktop\", DeviceId=\"dev-123\", Version=\"1.0\", Token=\"abc\""
        };

        Assert.Equal("dev-123", AuthHeaderHelper.ExtractDeviceId(headers));
        Assert.Equal("Feishin", AuthHeaderHelper.ExtractClientName(headers));
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
