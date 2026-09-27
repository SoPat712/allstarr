using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class AdminSessionCookiesTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("/allstarr", "/allstarr")]
    public void SessionCookiesStayInAdminScope(string prefix, string cookiePath)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = prefix;
        context.Request.Scheme = "https";
        AdminSessionCookies.Write(context, "fixture", DateTime.UtcNow.AddHours(1));
        var cookie = Assert.Single(context.Response.Headers.SetCookie)!;
        Assert.Contains("path=" + cookiePath + ";", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        context.Response.Headers.Clear();
        AdminSessionCookies.Delete(context);
        Assert.Equal(3, context.Response.Headers.SetCookie.Count);
        Assert.All(context.Response.Headers.SetCookie, value => Assert.Contains("path=" + cookiePath, value));
    }

    [Fact]
    public void UntrustedForwardedHeaderDoesNotControlCookieSecurity()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        AdminSessionCookies.Write(context, "fixture", DateTime.UtcNow.AddHours(1));
        Assert.DoesNotContain("; secure", Assert.Single(context.Response.Headers.SetCookie)!);
    }
}
