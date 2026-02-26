using System.Reflection;
using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinQueryRedactionTests
{
    [Fact]
    public void MaskSensitiveQueryString_RedactsSensitiveValues()
    {
        var masked = InvokeMaskSensitiveQueryString(
            "?api_key=secret1&query=hello&x-emby-token=secret2&AuthToken=secret3");

        Assert.Contains("api_key=<redacted>", masked);
        Assert.Contains("query=hello", masked);
        Assert.Contains("x-emby-token=<redacted>", masked);
        Assert.Contains("AuthToken=<redacted>", masked);
        Assert.DoesNotContain("secret1", masked);
        Assert.DoesNotContain("secret2", masked);
        Assert.DoesNotContain("secret3", masked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MaskSensitiveQueryString_EmptyOrNull_ReturnsEmpty(string? input)
    {
        var masked = InvokeMaskSensitiveQueryString(input);
        Assert.Equal(string.Empty, masked);
    }

    private static string InvokeMaskSensitiveQueryString(string? queryString)
    {
        var method = typeof(JellyfinController).GetMethod(
            "MaskSensitiveQueryString",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = method!.Invoke(null, new object?[] { queryString });
        return Assert.IsType<string>(result);
    }
}
