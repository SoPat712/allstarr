using System.Diagnostics;
using System.Net;
using System.Reflection;
using allstarr.Controllers;
using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace allstarr.Tests;

public sealed class AdminSignInRateLimitingTests
{
    [Fact]
    public void SubmittedPasswordActionsDeclareTheNamedPolicy()
    {
        var actions = new[]
        {
            (typeof(AdminAuthController), nameof(AdminAuthController.Login)),
            (typeof(AdminAuthController), nameof(AdminAuthController.GrantPlaylistConsent)),
            (typeof(BackendSetupController), nameof(BackendSetupController.Save)),
            (typeof(JellyfinController), nameof(JellyfinController.AuthenticateByName))
        };

        foreach (var (controller, action) in actions)
        {
            var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<HttpPostAttribute>());
            var policy = method.GetCustomAttribute<EnableRateLimitingAttribute>();
            Assert.Equal(AdminSignInRateLimiting.PolicyName, policy?.PolicyName);
        }
    }

    [Fact]
    public async Task PerAddressBudgetUsesNormalizedRemoteIpAndRejectsBeforeAction()
    {
        await using var fixture = await RateLimitFixture.StartAsync(perAddressLimit: 2, globalLimit: 20);

        using var first = await fixture.PostAsync(
            "/allstarr/api/admin/auth/login",
            "::ffff:203.0.113.17",
            forwardedFor: "198.51.100.1");
        using var second = await fixture.PostAsync(
            "/allstarr/API/ADMIN/AUTH/NATIVE-LOGIN/",
            "203.0.113.17",
            forwardedFor: "198.51.100.2");
        using var rejected = await fixture.PostAsync(
            "/allstarr/api/admin/auth/login",
            "203.0.113.17",
            forwardedFor: "198.51.100.3");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(2, fixture.AcceptedActions);
        Assert.True(TryReadPositiveRetryAfter(rejected));
        Assert.Equal("{\"error\":\"Too many sign-in attempts. Try again later.\"}",
            await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GlobalBudgetIsSharedAcrossAddressesAndLeavesNativeRoutesUnthrottled()
    {
        await using var fixture = await RateLimitFixture.StartAsync(perAddressLimit: 20, globalLimit: 3);

        foreach (var address in new[] { "203.0.113.21", "203.0.113.22", "203.0.113.23" })
        {
            using var accepted = await fixture.PostAsync(
                "/allstarr/api/admin/auth/login", address, forwardedFor: address);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var rejected = await fixture.PostAsync(
            "/allstarr/api/admin/auth/login",
            "203.0.113.24",
            forwardedFor: "203.0.113.24");
        using var native = await fixture.GetAsync(
            "/native?Download=true&Range=0-3",
            "203.0.113.24",
            forwardedFor: "198.51.100.24");
        using var oidc = await fixture.GetAsync(
            "/api/admin/auth/oidc/complete",
            "203.0.113.24",
            forwardedFor: "198.51.100.25");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.Equal("native-bytes", await native.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, oidc.StatusCode);
        Assert.Equal("oidc", await oidc.Content.ReadAsStringAsync());
        Assert.Equal(3, fixture.AcceptedActions);
    }

    [Fact]
    public async Task ForwardedHeadersUseOnlyTrustedProxyAndNormalizeForwardedAddresses()
    {
        await using var untrusted = await RateLimitFixture.StartAsync(
            perAddressLimit: 1,
            globalLimit: 20);
        using var untrustedFirst = await untrusted.PostAsync(
            "/allstarr/api/admin/auth/login",
            "198.51.100.50",
            forwardedFor: "203.0.113.50");
        using var untrustedRotated = await untrusted.PostAsync(
            "/allstarr/api/admin/auth/login",
            "198.51.100.50",
            forwardedFor: "203.0.113.51");

        await using var trusted = await RateLimitFixture.StartAsync(
            perAddressLimit: 1,
            globalLimit: 20);
        using var trustedFirst = await trusted.PostAsync(
            "/allstarr/api/admin/auth/login",
            RateLimitFixture.TrustedProxyAddress,
            forwardedFor: "203.0.113.60");
        using var trustedSecondSource = await trusted.PostAsync(
            "/allstarr/api/admin/auth/login",
            RateLimitFixture.TrustedProxyAddress,
            forwardedFor: "203.0.113.61");

        await using var mapped = await RateLimitFixture.StartAsync(
            perAddressLimit: 1,
            globalLimit: 20);
        using var mappedFirst = await mapped.PostAsync(
            "/allstarr/api/admin/auth/login",
            RateLimitFixture.TrustedProxyAddress,
            forwardedFor: "::ffff:203.0.113.70");
        using var mappedEquivalent = await mapped.PostAsync(
            "/allstarr/api/admin/auth/login",
            RateLimitFixture.TrustedProxyAddress,
            forwardedFor: "203.0.113.70");

        Assert.Equal(HttpStatusCode.OK, untrustedFirst.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, untrustedRotated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, trustedFirst.StatusCode);
        Assert.Equal(HttpStatusCode.OK, trustedSecondSource.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mappedFirst.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, mappedEquivalent.StatusCode);
    }

    [Fact]
    public async Task OrdinaryProxyAndUnknownAddressSignInsUseSeparatePartitions()
    {
        await using var fixture = await RateLimitFixture.StartAsync(perAddressLimit: 1, globalLimit: 20);
        using var before = await fixture.GetAsync("/native", "unknown", string.Empty);
        using var first = await fixture.PostAsync("/allstarr/api/admin/auth/login", "unknown", string.Empty);
        using var rejected = await fixture.PostAsync("/allstarr/api/admin/auth/login", "unknown", string.Empty);
        using var after = await fixture.GetAsync("/native", "unknown", string.Empty);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(1, fixture.AcceptedActions);
    }

    [Fact]
    public async Task RejectedRequestDoesNotWaitBehindHeldSignIns()
    {
        await using var fixture = await RateLimitFixture.StartAsync(perAddressLimit: 2, globalLimit: 20);
        fixture.HoldActions();
        var firstTask = fixture.PostAsync(
            "/allstarr/api/admin/auth/login",
            "203.0.113.31",
            forwardedFor: "203.0.113.31");
        var secondTask = fixture.PostAsync(
            "/allstarr/api/admin/auth/native-login",
            "203.0.113.31",
            forwardedFor: "203.0.113.32");

        try
        {
            await fixture.WaitForHeldActionsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var rejected = await fixture.PostAsync(
                    "/allstarr/api/admin/auth/login",
                    "203.0.113.31",
                    forwardedFor: "203.0.113.33")
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(2, fixture.AcceptedActions);
        }
        finally
        {
            fixture.ReleaseHeldActions();
        }

        using var first = await firstTask;
        using var second = await secondTask;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task ConfiguredWindowReplenishesTheAddressBudget()
    {
        await using var fixture = await RateLimitFixture.StartAsync(
            perAddressLimit: 1,
            globalLimit: 10,
            window: TimeSpan.FromSeconds(5));

        using var first = await fixture.PostAsync(
            "/allstarr/api/admin/auth/login",
            "203.0.113.41",
            forwardedFor: "203.0.113.41");
        using var blocked = await fixture.PostAsync(
            "/allstarr/api/admin/auth/login",
            "203.0.113.41",
            forwardedFor: "203.0.113.42");
        using var recovered = await WaitForRecoveryAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/auth/login")]
    [InlineData("/API/ADMIN/AUTH/LOGIN/")]
    [InlineData("/api/admin/auth/setup")]
    [InlineData("/api/admin/auth/playlist-consent")]
    [InlineData("/Users/AuthenticateByName")]
    [InlineData("/users/authenticatebyname/")]
    [InlineData("/Users/AuthenticateByName///")]
    [InlineData("http://allstarr.test//Users/AuthenticateByName")]
    [InlineData("http://allstarr.test///users/authenticatebyname///")]
    [InlineData("/Users/AuthenticateByName%20")]
    public async Task ActualPasswordControllerRoutesRejectBeforeConstructingControllers(string path)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupFilter, TestRateLimiterCleanup>();
                    services.AddControllers().AddApplicationPart(typeof(AdminAuthController).Assembly)
                        .ConfigureApplicationPartManager(manager =>
                        {
                            var standard = manager.FeatureProviders
                                .OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>().Single();
                            manager.FeatureProviders.Remove(standard);
                            manager.FeatureProviders.Add(new BackendControllerFeatureProvider(
                                allstarr.Models.Settings.BackendType.Jellyfin,
                                allstarr.Core.Configuration.ReleaseComposition.Core));
                        });
                    services.AddAdminSignInRateLimiting(TimeSpan.FromMinutes(1), 1, 20);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/fixture/sign-in", context => context.Response.WriteAsync("accepted"))
                            .RequireRateLimiting(AdminSignInRateLimiting.PolicyName);
                        endpoints.MapControllers();
                    });
                }))
            .StartAsync();
        using var client = host.GetTestClient();
        using var accepted = await client.PostAsync("/fixture/sign-in", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // No controller dependencies are registered: rejection must precede activation and body binding.
        using var rejected = await client.PostAsync(path, new StringContent("not valid JSON"));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(TryReadPositiveRetryAfter(rejected));
    }

    private static async Task<HttpResponseMessage> WaitForRecoveryAsync(RateLimitFixture fixture)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(12).TotalSeconds);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            var response = await fixture.PostAsync(
                "/allstarr/api/admin/auth/login",
                "203.0.113.41",
                forwardedFor: "203.0.113.43");
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;
            response.Dispose();
        }

        throw new TimeoutException("The short fixed-window limiter did not replenish within 12 seconds.");
    }

    private static bool TryReadPositiveRetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.TryGetValues("Retry-After", out var values)
            ? values.SingleOrDefault()
            : null;
        return int.TryParse(value, out var seconds) && seconds > 0;
    }

    private sealed class RateLimitFixture : IAsyncDisposable
    {
        public const string TrustedProxyAddress = "192.0.2.1";

        private readonly IHost host;
        private readonly HttpClient client;
        private readonly RateLimitFixtureAccessor accessor;

        private RateLimitFixture(IHost host, RateLimitFixtureAccessor accessor)
        {
            this.host = host;
            this.accessor = accessor;
            client = host.GetTestClient();
            client.BaseAddress = new Uri("http://allstarr.test");
        }

        public int AcceptedActions => Volatile.Read(ref accessor.AcceptedActions);

        public static async Task<RateLimitFixture> StartAsync(
            int perAddressLimit,
            int globalLimit,
            TimeSpan? window = null)
        {
            var accessor = new RateLimitFixtureAccessor();
            var host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IStartupFilter, TestRateLimiterCleanup>();
                        services.AddRouting();
                        services.AddSingleton(accessor);
                        services.AddSingleton(new AdminBasePath(new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                [AdminBasePath.ConfigurationKey] = "/allstarr"
                            })
                            .Build()));
                        services.AddAdminSignInRateLimiting(
                            window ?? TimeSpan.FromMinutes(1), perAddressLimit, globalLimit);
                        services.Configure<ForwardedHeadersOptions>(options =>
                        {
                            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                            options.ForwardLimit = 2;
                            options.KnownIPNetworks.Clear();
                            options.KnownProxies.Clear();
                            options.KnownProxies.Add(IPAddress.Parse(TrustedProxyAddress));
                        });
                    })
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            context.Connection.LocalPort = 5275;
                            if (IPAddress.TryParse(
                                    context.Request.Headers["X-Test-Remote-Ip"].ToString(),
                                    out var remoteIp))
                            {
                                context.Connection.RemoteIpAddress = remoteIp;
                            }

                            await next(context);
                        });
                        app.UseForwardedHeaders();
                        app.UseMiddleware<AdminBasePathMiddleware>();
                        app.UseRouting();
                        app.UseRateLimiter();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/api/admin/auth/login", async context =>
                            {
                                await WriteAcceptedAsync(context, accessor, "accepted");
                            }).RequireRateLimiting(AdminSignInRateLimiting.PolicyName);
                            endpoints.MapPost("/api/admin/auth/native-login", async context =>
                            {
                                await WriteAcceptedAsync(context, accessor, "accepted-native");
                            }).RequireRateLimiting(AdminSignInRateLimiting.PolicyName);
                            endpoints.MapGet("/native", context =>
                            {
                                context.Response.ContentType = "application/octet-stream";
                                return context.Response.WriteAsync("native-bytes");
                            }).WithMetadata(new NativeSignInFallbackAttribute())
                                .RequireRateLimiting(AdminSignInRateLimiting.PolicyName);
                            endpoints.MapGet("/api/admin/auth/oidc/complete", context =>
                                context.Response.WriteAsync("oidc"));
                        });
                    }))
                .StartAsync();

            var fixture = new RateLimitFixture(host, accessor);
            return fixture;
        }

        private static async Task WriteAcceptedAsync(
            HttpContext context,
            RateLimitFixtureAccessor accessor,
            string body)
        {
            Interlocked.Increment(ref accessor.AcceptedActions);
            if (accessor.HoldActions && Interlocked.Increment(ref accessor.HeldActions) == 2)
                accessor.AllHeld.TrySetResult();
            if (accessor.HoldActions)
                await accessor.Release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(body);
        }

        public Task<HttpResponseMessage> PostAsync(string path, string remoteIp, string forwardedFor) =>
            SendAsync(HttpMethod.Post, path, remoteIp, forwardedFor);

        public Task<HttpResponseMessage> GetAsync(string path, string remoteIp, string forwardedFor) =>
            SendAsync(HttpMethod.Get, path, remoteIp, forwardedFor);

        public void HoldActions() => accessor.HoldActions = true;

        public Task WaitForHeldActionsAsync() => accessor.AllHeld.Task;

        public void ReleaseHeldActions() => accessor.Release.TrySetResult();

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string path, string remoteIp, string forwardedFor)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Test-Remote-Ip", remoteIp);
            if (!string.IsNullOrEmpty(forwardedFor))
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            host.Dispose();
            await Task.CompletedTask;
        }
    }

    private sealed class RateLimitFixtureAccessor
    {
        public int AcceptedActions;
        public int HeldActions;
        public bool HoldActions;
        public TaskCompletionSource AllHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
