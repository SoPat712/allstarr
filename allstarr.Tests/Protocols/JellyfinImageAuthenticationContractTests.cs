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
    [InlineData("GET", "/Artists/artist-id/Images/Primary/0", true)]
    [InlineData("HEAD", "/Genres/Rock/Images/Primary", true)]
    [InlineData("GET", "/MusicGenres/Rock/Images/Primary", true)]
    [InlineData("GET", "/UserImage", true)]
    [InlineData("GET", "/Users/Public", true)]
    [InlineData("GET", "/System/Ping", true)]
    [InlineData("POST", "/System/Ping", true)]
    [InlineData("GET", "/GetUtcTime", true)]
    [InlineData("GET", "/QuickConnect/Enabled", true)]
    [InlineData("GET", "/QuickConnect/Connect", true)]
    [InlineData("POST", "/QuickConnect/Initiate", true)]
    [InlineData("POST", "/Users/AuthenticateWithQuickConnect", true)]
    [InlineData("POST", "/QuickConnect/Authorize", false)]
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
