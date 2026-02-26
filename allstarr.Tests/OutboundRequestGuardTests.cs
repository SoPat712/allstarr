using allstarr.Services.Common;

namespace allstarr.Tests;

public class OutboundRequestGuardTests
{
    [Fact]
    public void TryCreateSafeHttpUri_WithPublicHttpsUrl_AllowsRequest()
    {
        var allowed = OutboundRequestGuard.TryCreateSafeHttpUri(
            "https://example.com/cover.jpg",
            out var uri,
            out var reason);

        Assert.True(allowed);
        Assert.NotNull(uri);
        Assert.Equal("https://example.com/cover.jpg", uri!.ToString());
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("http://localhost/test")]
    [InlineData("http://127.0.0.1/test")]
    [InlineData("http://10.0.0.5/album.png")]
    [InlineData("http://192.168.1.10/album.png")]
    [InlineData("http://100.64.0.25/path")]
    [InlineData("http://[::1]/image")]
    [InlineData("http://[fd00::1]/image")]
    public void TryCreateSafeHttpUri_WithLocalOrPrivateHost_BlocksRequest(string rawUrl)
    {
        var allowed = OutboundRequestGuard.TryCreateSafeHttpUri(rawUrl, out var uri, out var reason);

        Assert.False(allowed);
        Assert.Null(uri);
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path")]
    public void TryCreateSafeHttpUri_WithInvalidSchemeOrRelativeUrl_BlocksRequest(string rawUrl)
    {
        var allowed = OutboundRequestGuard.TryCreateSafeHttpUri(rawUrl, out var uri, out var reason);

        Assert.False(allowed);
        Assert.Null(uri);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void TryCreateSafeHttpUri_WithUserInfo_BlocksRequest()
    {
        var allowed = OutboundRequestGuard.TryCreateSafeHttpUri(
            "https://user:pass@example.com/image.jpg",
            out var uri,
            out var reason);

        Assert.False(allowed);
        Assert.Null(uri);
        Assert.Contains("Userinfo", reason, StringComparison.OrdinalIgnoreCase);
    }
}
