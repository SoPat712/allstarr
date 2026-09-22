using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace allstarr.Tests;

public class AuthHeaderHelperTests
{
    [Fact]
    public void ForwardAuthHeaders_ShouldPreferModernAuthorization()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] = "MediaBrowser Token=\"abc\"",
            ["Authorization"] = "Bearer xyz"
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.True(request.Headers.TryGetValues("Authorization", out var values));
        Assert.Contains("Bearer xyz", values);
        Assert.False(request.Headers.Contains("X-Emby-Authorization"));
    }

    [Fact]
    public void BuildForwardedAuthorization_ShouldPreferModernAuthorization()
    {
        var headers = new HeaderDictionary
        {
            ["Authorization"] = "MediaBrowser DeviceId=\"modern\", Token=\"new\"",
            ["X-Emby-Authorization"] = "MediaBrowser DeviceId=\"legacy\", Token=\"old\""
        };

        Assert.Equal(
            "MediaBrowser DeviceId=\"modern\", Token=\"new\"",
            AuthHeaderHelper.BuildForwardedAuthorization(headers));
        Assert.Equal("modern", AuthHeaderHelper.ExtractDeviceId(headers));
    }

    [Fact]
    public void BuildForwardedAuthorization_ShouldNormalizeLegacyAuthorization()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Authorization"] =
                "MediaBrowser DeviceId=\"legacy-device\", Token=\"legacy-token\""
        };

        Assert.Equal(
            "MediaBrowser DeviceId=\"legacy-device\", Token=\"legacy-token\"",
            AuthHeaderHelper.BuildForwardedAuthorization(headers));
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
        Assert.True(request.Headers.TryGetValues("Authorization", out var values));
        Assert.Contains("MediaBrowser Client=\"Feishin\", Token=\"abc\"", values);
        Assert.False(request.Headers.Contains("X-Emby-Authorization"));
        Assert.False(request.Headers.Contains("X-Emby-Token"));
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
    public void ForwardAuthHeaders_ShouldUpgradeXEmbyToken()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Token"] = "abc"
        };

        using var request = new HttpRequestMessage();
        var forwarded = AuthHeaderHelper.ForwardAuthHeaders(headers, request);

        Assert.True(forwarded);
        Assert.True(request.Headers.TryGetValues("Authorization", out var values));
        Assert.Contains(values, value => value.Contains("Token=\"abc\"", StringComparison.Ordinal));
        Assert.False(request.Headers.Contains("X-Emby-Token"));
    }

    [Fact]
    public void BuildForwardedAuthorization_ShouldUpgradeTokenWithCallerDeviceId()
    {
        var headers = new HeaderDictionary
        {
            ["X-Emby-Token"] = "abc"
        };

        var authorization = AuthHeaderHelper.BuildForwardedAuthorization(headers, "client-device");

        Assert.NotNull(authorization);
        Assert.StartsWith("MediaBrowser ", authorization, StringComparison.Ordinal);
        Assert.Contains("DeviceId=\"client-device\"", authorization, StringComparison.Ordinal);
        Assert.Contains($"Version=\"{allstarr.AppVersion.Version}\"", authorization, StringComparison.Ordinal);
        Assert.Contains("Token=\"abc\"", authorization, StringComparison.Ordinal);
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
