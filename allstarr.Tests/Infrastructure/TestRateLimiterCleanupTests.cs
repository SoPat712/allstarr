using System.Net;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace allstarr.Tests;

public sealed class TestRateLimiterCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostDisposalReleasesBothRateLimiters(bool asynchronously)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAdminSignInRateLimiting();
                services.AddSingleton<IStartupFilter, TestRateLimiterCleanup>();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(endpoints => endpoints.MapPost("/api/admin/auth/login",
                    context => context.Response.WriteAsync("accepted"))
                    .RequireRateLimiting(AdminSignInRateLimiting.PolicyName));
            })).StartAsync();
        using var client = host.GetTestClient();
        using var response = await client.PostAsync("/api/admin/auth/login", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cleanup = Assert.Single(host.Services.GetServices<IStartupFilter>().OfType<TestRateLimiterCleanup>());
        var limiters = cleanup.Limiters.ToArray();
        Assert.Equal(2, limiters.Length);

        if (asynchronously) await ((IAsyncDisposable)host).DisposeAsync();
        else host.Dispose();

        Assert.Empty(cleanup.Limiters);
        Assert.All(limiters, limiter =>
            Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire(new DefaultHttpContext())));
    }
}
