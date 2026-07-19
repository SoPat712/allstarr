using System.Reflection;
using allstarr.Filters;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class JellyfinImageAuthenticationContractTests
{
    [Theory]
    [InlineData("GET", "/Items/track-id/Images/Primary", true)]
    [InlineData("HEAD", "/Users/user-id/Images/Primary", true)]
    [InlineData("GET", "/Items/track-id/Images/Primary/0", true)]
    [InlineData("POST", "/Items/track-id/Images/Primary", false)]
    [InlineData("GET", "/Items/track-id/File", false)]
    [InlineData("GET", "/Users/user-id", false)]
    public void PublicBootstrapPolicy_MirrorsJellyfinImageAccess(
        string method,
        string path,
        bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;
        var policy = typeof(JellyfinAuthFilter).GetMethod(
            "IsPublicBootstrapRequest",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(policy);
        Assert.Equal(expected, policy.Invoke(null, [request]));
    }
}
