using allstarr.Controllers;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AdminOidcTransportTests
{
    [Theory]
    [InlineData("127.0.0.2")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.116")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("[::1]")]
    [InlineData("[fc00::1]")]
    [InlineData("[fdff::1]")]
    [InlineData("[fe80::1]")]
    [InlineData("[febf::1]")]
    [InlineData("localhost")]
    [InlineData("identity.local")]
    [InlineData("identity.lan")]
    [InlineData("identity.home.arpa")]
    [InlineData("IDENTITY.INTERNAL")]
    public void PrivateHostsAllowHttp(string host)
    {
        Assert.True(AdminOidcOptions.IsAllowedUrl("http://" + host, out _));
        Assert.True(AdminOidcOptions.IsAllowedUrl("https://" + host, out _));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.255.0.1")]
    [InlineData("[2001:4860:4860::8888]")]
    [InlineData("[fe7f::1]")]
    [InlineData("[fec0::1]")]
    [InlineData("identity.example.test")]
    [InlineData("localhost.example.test")]
    [InlineData("notlocal")]
    [InlineData("identity.lan.example.test")]
    public void PublicHostsRequireHttps(string host)
    {
        Assert.False(AdminOidcOptions.IsAllowedUrl("http://" + host, out _));
        Assert.True(AdminOidcOptions.IsAllowedUrl("https://" + host, out _));
    }

    [Theory]
    [InlineData("http://user@localhost")]
    [InlineData("http://localhost?query=value")]
    [InlineData("http://localhost#fragment")]
    [InlineData("ftp://localhost")]
    public void OtherUrlRestrictionsStillApply(string value) =>
        Assert.False(AdminOidcOptions.IsAllowedUrl(value, out _));

    [Theory]
    [InlineData("http://identity.lan", "http://192.168.1.116:5275/allstarr/")]
    [InlineData("https://identity.example.test", "http://localhost:5275/allstarr")]
    [InlineData("http://identity.internal", "https://admin.example.test/allstarr")]
    public void PrivateHttpConfigurationKeepsTheAdminPath(string authority, string publicUrl)
    {
        var options = new AdminOidcOptions { Enabled = true, Authority = authority, PublicUrl = publicUrl, ClientId = "fixture", ClientSecret = "fixture" };
        options.Validate("/allstarr");
        Assert.Equal(publicUrl.TrimEnd('/'), options.PublicUrl);
        options.PublicUrl = publicUrl.TrimEnd('/') + "/wrong";
        Assert.Throws<InvalidOperationException>(() => options.Validate("/allstarr"));
    }

    [Theory]
    [InlineData("http://localhost:5275", "http://identity.lan", false)]
    [InlineData("http://localhost:5275", "https://identity.test", true)]
    [InlineData("https://admin.test", "http://identity.lan", false)]
    [InlineData("https://admin.test", "https://identity.test", true)]
    public void CookiesAndMetadataFollowTheirConfiguredAddresses(string publicUrl, string authority, bool requireHttpsMetadata)
    {
        using var services = Services(publicUrl, authority);
        var privateHttp = publicUrl.StartsWith("http:", StringComparison.Ordinal);
        var secure = privateHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        var pending = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminOidcOptions.PendingScheme);
        var oidc = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AdminOidcOptions.Scheme);
        var csrf = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        Assert.Equal(secure, pending.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, pending.Cookie.SameSite);
        Assert.Equal(secure, csrf.Cookie.SecurePolicy);
        Assert.Equal(privateHttp ? SameSiteMode.Lax : SameSiteMode.Strict, csrf.Cookie.SameSite);
        Assert.Equal(secure, oidc.CorrelationCookie.SecurePolicy);
        Assert.Equal(secure, oidc.NonceCookie.SecurePolicy);
        Assert.Equal(privateHttp ? SameSiteMode.Lax : SameSiteMode.None, oidc.CorrelationCookie.SameSite);
        Assert.Equal(privateHttp ? SameSiteMode.Lax : SameSiteMode.None, oidc.NonceCookie.SameSite);
        Assert.Equal(requireHttpsMetadata, oidc.RequireHttpsMetadata);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = privateHttp ? "http" : "https";
        AdminSessionCookies.Write(context, "fixture", DateTime.UtcNow.AddHours(1));
        var cookie = Assert.Single(context.Response.Headers.SetCookie)!;
        Assert.Equal(!privateHttp, cookie.Contains("; secure", StringComparison.Ordinal));
        Assert.Contains(privateHttp ? "samesite=lax" : "samesite=strict", cookie);
    }

    [Theory]
    [InlineData("http://localhost:5275", "http", 5275, true)]
    [InlineData("http://localhost:5275", "https", 5275, true)]
    [InlineData("https://admin.test", "http", 5275, false)]
    [InlineData("https://admin.test", "https", 5275, true)]
    [InlineData("http://localhost:5275", "http", 5274, false)]
    [InlineData("http://localhost:5275", "https", 5274, false)]
    public async Task RequestChecksKeepHttpOnThePrivateAdminListener(string publicUrl, string scheme, int port, bool allowed)
    {
        using var services = Services(publicUrl, "https://identity.test");
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = scheme;
        context.Connection.LocalPort = port;
        var options = services.GetRequiredService<AdminOidcOptions>();
        Assert.Equal(allowed, options.IsRequestAllowed(context.Request));
        var authentication = new Mock<IAuthenticationService>();
        authentication.Setup(item => item.AuthenticateAsync(context, AdminOidcOptions.PendingScheme)).ReturnsAsync(AuthenticateResult.NoResult());
        context.RequestServices = new ServiceCollection().AddSingleton(authentication.Object).BuildServiceProvider();
        var controller = new AdminOidcController(options, null!, null!, null!, NullLogger<AdminOidcController>.Instance)
        { ControllerContext = new() { HttpContext = context } };
        var login = await controller.Login();
        var complete = await controller.Complete(default);
        Assert.Equal(allowed, login is ChallengeResult);
        Assert.Equal(allowed, complete is RedirectResult);
        using var client = new HttpClient();
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var authController = new AdminAuthController(Options.Create(new JellyfinSettings()), Options.Create(new SubsonicSettings()),
            new ConfigurationBuilder().Build(), clientFactory.Object, null!, NullLogger<AdminAuthController>.Instance, null!, oidcOptions: options)
        { ControllerContext = new() { HttpContext = context } };
        var linking = Assert.IsType<BadRequestObjectResult>(await authController.Login(new() { LinkOidc = true }));
        Assert.Equal(allowed, System.Text.Json.JsonSerializer.Serialize(linking.Value).Contains("SSO is not enabled.", StringComparison.Ordinal));
        var oidc = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AdminOidcOptions.Scheme);
        var message = new MessageReceivedContext(context, new AuthenticationScheme(AdminOidcOptions.Scheme, null, typeof(OpenIdConnectHandler)), oidc, new());
        await oidc.Events.MessageReceived(message);
        if (port != 5275) Assert.True(message.Result?.Skipped);
        else Assert.Equal(!allowed, message.Result?.Failure != null);
    }

    private static ServiceProvider Services(string publicUrl, string authority)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:Oidc:Enabled"] = "true",
            ["Admin:Oidc:Authority"] = authority,
            ["Admin:Oidc:PublicUrl"] = publicUrl,
            ["Admin:Oidc:ClientId"] = "fixture",
            ["Admin:Oidc:ClientSecret"] = "fixture"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminOidc(configuration);
        return services.BuildServiceProvider();
    }
}
