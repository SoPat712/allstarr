using System.Reflection;
using allstarr.Filters;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class JellyfinApiKeyUserAuthTests
{
    [Fact]
    public void ExplicitRouteUser_IsUsedForCredentialVerification()
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["userId"] = "1635cd7d23144ba08251ebe22a56119e";

        var endpoint = InvokeBuildCurrentUserEndpoint(context.Request);

        Assert.Equal("Users/1635cd7d23144ba08251ebe22a56119e", endpoint);
    }

    [Fact]
    public void ExplicitQueryUser_IsUsedForCredentialVerification()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?UserId=backend-user-1");

        var endpoint = InvokeBuildCurrentUserEndpoint(context.Request);

        Assert.Equal("Users/backend-user-1", endpoint);
    }

    [Fact]
    public void UnsafeOrMissingUser_FallsBackToCurrentUser()
    {
        var unsafeContext = new DefaultHttpContext();
        unsafeContext.Request.RouteValues["userId"] = "../admin";
        var missingContext = new DefaultHttpContext();

        Assert.Equal("Users/Me", InvokeBuildCurrentUserEndpoint(unsafeContext.Request));
        Assert.Equal("Users/Me", InvokeBuildCurrentUserEndpoint(missingContext.Request));
    }

    private static string InvokeBuildCurrentUserEndpoint(HttpRequest request)
    {
        var method = typeof(JellyfinAuthFilter).GetMethod(
            "BuildCurrentUserEndpoint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [request])!;
    }
}
