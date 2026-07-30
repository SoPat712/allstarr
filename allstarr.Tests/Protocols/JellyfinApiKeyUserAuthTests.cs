using System.Reflection;
using allstarr.Filters;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class JellyfinApiKeyUserAuthTests
{
    [Fact]
    public void CurrentUserVerification_DoesNotTrustDeclaredUser()
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["userId"] = "1635cd7d23144ba08251ebe22a56119e";
        context.Request.QueryString = new QueryString("?api_key=fixture");

        var endpoint = InvokeBuildCurrentUserEndpoint(context.Request);

        Assert.Equal("Users/Me?api_key=fixture", endpoint);
    }

    [Fact]
    public void ExplicitRouteOrQueryUser_IsAvailableOnlyForUnboundApiKeyFallback()
    {
        var routeContext = new DefaultHttpContext();
        routeContext.Request.RouteValues["userId"] = "backend-user-1";
        var queryContext = new DefaultHttpContext();
        queryContext.Request.QueryString = new QueryString("?UserId=backend-user-2&api_key=fixture");

        Assert.Equal("Users/backend-user-1", InvokeBuildExplicitUserEndpoint(routeContext.Request));
        Assert.Equal(
            "Users/backend-user-2?api_key=fixture",
            InvokeBuildExplicitUserEndpoint(queryContext.Request));
    }

    [Fact]
    public void UnsafeOrMissingExplicitUser_HasNoFallback()
    {
        var unsafeContext = new DefaultHttpContext();
        unsafeContext.Request.RouteValues["userId"] = "../admin";
        var missingContext = new DefaultHttpContext();

        Assert.Null(InvokeBuildExplicitUserEndpoint(unsafeContext.Request));
        Assert.Null(InvokeBuildExplicitUserEndpoint(missingContext.Request));
    }

    private static string InvokeBuildCurrentUserEndpoint(HttpRequest request)
    {
        var method = typeof(JellyfinAuthFilter).GetMethod(
            "BuildCurrentUserEndpoint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [request])!;
    }

    private static string? InvokeBuildExplicitUserEndpoint(HttpRequest request)
    {
        var method = typeof(JellyfinAuthFilter).GetMethod(
            "BuildExplicitUserEndpoint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [request]);
    }
}
