using System.Net;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class JellyfinAuthVerificationTests
{
    [Fact]
    public async Task RepeatedRequestsReuseVerificationAndResolvedPrincipal()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var contexts = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        contexts.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AllstarrDbContext(database.Options));
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var resolver = new BackendIdentityResolver(contexts.Object, state, new IdentityOptions(), new SystemPlatformClock());
        var backend = new Backend();
        var filter = CreateFilter(backend, resolver: resolver);
        AllstarrPrincipal? first = null;
        for (var index = 0; index < 10; index++)
        {
            var context = await Execute(filter);
            var principal = Assert.IsType<AllstarrPrincipal>(context.HttpContext.Items[BackendIdentityResolver.HttpContextPrincipalItemKey]);
            first ??= principal;
            Assert.Same(first, principal);
            Assert.Equal("listener", context.HttpContext.Items[JellyfinAuthFilter.BackendPrincipalIdItemKey]);
        }
        Assert.Equal(1, backend.Verifications);
        contexts.Verify(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CachedVerificationResolvesPrincipalWhenStorageBecomesReady()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var contexts = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        contexts.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AllstarrDbContext(database.Options));
        var state = new DurableStorageState(database.StorageOptions);
        var resolver = new BackendIdentityResolver(contexts.Object, state, new IdentityOptions(), new SystemPlatformClock());
        var backend = new Backend();
        var filter = CreateFilter(backend, resolver: resolver);
        Assert.False((await Execute(filter)).HttpContext.Items.ContainsKey(BackendIdentityResolver.HttpContextPrincipalItemKey));
        contexts.VerifyNoOtherCalls();
        state.Set(DurableStorageReadiness.Ready);
        var resolved = await Execute(filter);
        var repeated = await Execute(filter);
        Assert.Same(resolved.HttpContext.Items[BackendIdentityResolver.HttpContextPrincipalItemKey],
            repeated.HttpContext.Items[BackendIdentityResolver.HttpContextPrincipalItemKey]);
        Assert.Equal(1, backend.Verifications);
        contexts.Verify(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailureAndMalformedSuccessAreNotCached()
    {
        var backend = new Backend { VerificationStatus = HttpStatusCode.Unauthorized };
        var filter = CreateFilter(backend);
        Assert.NotNull((await Execute(filter)).Result);
        Assert.NotNull((await Execute(filter)).Result);
        backend.VerificationStatus = HttpStatusCode.OK;
        backend.VerificationBody = "{}";
        Assert.NotNull((await Execute(filter)).Result);
        backend.VerificationBody = """{"Id":"listener"}""";
        Assert.Null((await Execute(filter)).Result);
        Assert.Null((await Execute(filter)).Result);
        Assert.Equal(4, backend.Verifications);
    }

    [Fact]
    public async Task VerificationExpiresTwoMinutesAfterSuccessWithoutSliding()
    {
        var clock = new Clock();
        var backend = new Backend();
        var filter = CreateFilter(backend, clock);
        await Execute(filter);
        clock.Now += TimeSpan.FromSeconds(119);
        await Execute(filter);
        Assert.Equal(1, backend.Verifications);
        clock.Now += TimeSpan.FromSeconds(1);
        await Execute(filter);
        Assert.Equal(2, backend.Verifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxiedUnauthorizedEvictsOnlyItsCredential(bool queryCredential)
    {
        var backend = new Backend();
        var proxy = CreateProxy(backend);
        var filter = CreateFilter(backend, proxy: proxy);
        var query = queryCredential ? "?api_key=first" : "";
        var headers = queryCredential ? null : "first";
        await Execute(filter, token: headers, query: query);
        await Execute(filter, token: "second");
        var request = Context(headers, query).HttpContext.Request;
        backend.RelayStatus = HttpStatusCode.Unauthorized;
        using (await proxy.SendPassthroughResponseAsync(request, "Items/test" + query)) { }
        backend.RelayStatus = HttpStatusCode.OK;
        await Execute(filter, token: "second");
        Assert.Equal(2, backend.Verifications);
        await Execute(filter, token: headers, query: query);
        Assert.Equal(3, backend.Verifications);
    }

    [Fact]
    public async Task ProxiedUnauthorizedEvictsConflictingCredentialCombinationsWithoutEvictingOtherTokens()
    {
        var backend = new Backend();
        var proxy = CreateProxy(backend);
        var filter = CreateFilter(backend, proxy: proxy);
        await Execute(filter, token: "first");
        await Execute(filter, token: "second");
        await Execute(filter, token: "first", query: "?api_key=second");
        backend.RelayStatus = HttpStatusCode.Unauthorized;
        using (await proxy.SendPassthroughResponseAsync(Context("second").HttpContext.Request, "Items/test")) { }
        await Execute(filter, token: "first");
        Assert.Equal(3, backend.Verifications);
        await Execute(filter, token: "second");
        await Execute(filter, token: "first", query: "?api_key=second");
        Assert.Equal(5, backend.Verifications);
    }

    [Fact]
    public async Task ExplicitUsersAndNativeFileRelayDoNotBindAnActorOrAuthorizeOtherPaths()
    {
        var backend = new Backend { VerificationStatus = HttpStatusCode.BadRequest };
        var contexts = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        var state = new DurableStorageState(new StorageOptions());
        state.Set(DurableStorageReadiness.Ready);
        var resolver = new BackendIdentityResolver(contexts.Object, state, new IdentityOptions(), new SystemPlatformClock());
        var filter = CreateFilter(backend, resolver: resolver);
        foreach (var user in new[] { "one", "one", "two" })
        {
            var context = await Execute(filter, token: null, query: "?api_key=first&userId=" + user);
            Assert.Equal(user, context.HttpContext.Items[JellyfinAuthFilter.BackendPrincipalIdItemKey]);
            Assert.False(context.HttpContext.Items.ContainsKey(BackendIdentityResolver.HttpContextPrincipalItemKey));
        }
        Assert.Equal(2, backend.Verifications);
        Assert.Equal(2, backend.ExplicitUsers);
        Assert.NotNull((await Execute(filter)).Result);
        await Execute(filter, path: "/Items/track/File");
        await Execute(filter, path: "/Items/track/Download");
        Assert.Equal(1, backend.NativeRelays);
        Assert.NotNull((await Execute(filter)).Result);
        Assert.Equal(5, backend.Verifications);
        contexts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConcurrentFirstRequestsCoalesceWithoutRetainingRawCredentials()
    {
        var backend = new Backend
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var filter = CreateFilter(backend);
        var requests = Enumerable.Range(0, 12).Select(_ => Execute(filter)).ToArray();
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, backend.Verifications);
        backend.Release.SetResult();
        await Task.WhenAll(requests);
        Assert.Equal(1, backend.Verifications);
        var request = Context("private-test-token").HttpContext.Request;
        var key = JellyfinVerificationCache.CredentialKey(request.Headers, request.QueryString.Value);
        Assert.Equal(64, key.Length);
        Assert.DoesNotContain("private-test-token", key);
    }

    [Fact]
    public async Task ForwardedAuthorizationPrecedenceAndQueryCredentialsAreIsolated()
    {
        var backend = new Backend();
        var filter = CreateFilter(backend);
        foreach (var token in new[] { "first", "second", "first" })
        {
            var context = Context("ignored-token");
            context.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
            await filter.OnActionExecutionAsync(context, () => Next(context));
        }
        Assert.Equal(2, backend.Verifications);
        await Execute(filter, token: "first", query: "?api_key=other");
        Assert.Equal(3, backend.Verifications);
    }

    [Fact]
    public async Task SameTokenAcrossHeaderAndQueryTransportsReusesVerification()
    {
        var backend = new Backend();
        var filter = CreateFilter(backend);
        await Execute(filter);
        await Execute(filter, token: null, query: "?api_key=first");
        await Execute(filter, query: "?api_key=first");
        var context = Context(null);
        context.HttpContext.Request.Headers["X-Emby-Authorization"] = "MediaBrowser Client=\"Aloud\", Device=\"Phone\", Token=\"first\"";
        await filter.OnActionExecutionAsync(context, () => Next(context));
        context = Context(null, "?api_key=first");
        context.HttpContext.Request.Headers["X-Emby-Authorization"] = "MediaBrowser Client=\"Aloud\", DeviceId=\"phone\"";
        await filter.OnActionExecutionAsync(context, () => Next(context));
        Assert.Equal(1, backend.Verifications);
    }

    [Fact]
    public async Task MissingCredentialsStillRejectAndBootstrapStaysPublic()
    {
        var backend = new Backend();
        var filter = CreateFilter(backend);
        Assert.IsType<UnauthorizedResult>((await Execute(filter, token: null)).Result);
        Assert.Null((await Execute(filter, token: null, path: "/System/Info/Public")).Result);
        Assert.Equal(0, backend.Verifications);
    }

    private static JellyfinAuthFilter CreateFilter(Backend backend, Clock? clock = null,
        BackendIdentityResolver? resolver = null, JellyfinProxyService? proxy = null) => new(
        proxy ?? CreateProxy(backend, clock),
        resolver ?? new BackendIdentityResolver(Mock.Of<IDbContextFactory<AllstarrDbContext>>(),
            new DurableStorageState(new StorageOptions()), new IdentityOptions(), new SystemPlatformClock()),
        NullLogger<JellyfinAuthFilter>.Instance);

    private static JellyfinProxyService CreateProxy(Backend backend, Clock? clock = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(new HttpClient(backend));
        var cache = new DisabledApplicationCache();
        return new JellyfinProxyService(factory.Object, Options.Create(new JellyfinSettings { Url = "http://backend.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build(), clock);
    }

    private static async Task<ActionExecutingContext> Execute(JellyfinAuthFilter filter, string? token = "first",
        string query = "", string path = "/Artists/AlbumArtists")
    {
        var context = Context(token, query, path);
        await filter.OnActionExecutionAsync(context, () => Next(context));
        return context;
    }

    private static ActionExecutingContext Context(string? token, string query = "", string path = "/Artists/AlbumArtists")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        if (token != null) http.Request.Headers["X-Emby-Token"] = token;
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [], new Dictionary<string, object?>(), new object());
    }

    private static Task<ActionExecutedContext> Next(ActionExecutingContext context) =>
        Task.FromResult(new ActionExecutedContext(context, [], context.Controller));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Backend : HttpMessageHandler
    {
        public int Verifications;
        public int ExplicitUsers;
        public int NativeRelays;
        public HttpStatusCode VerificationStatus = HttpStatusCode.OK;
        public HttpStatusCode RelayStatus = HttpStatusCode.OK;
        public string VerificationBody = """{"Id":"listener","Name":"Listener","Policy":{"IsAdministrator":false}}""";
        public TaskCompletionSource? Started;
        public TaskCompletionSource? Release;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = VerificationBody;
            var status = HttpStatusCode.OK;
            if (path == "/Users/Me")
            {
                Interlocked.Increment(ref Verifications);
                Started?.TrySetResult();
                if (Release != null) await Release.Task.WaitAsync(cancellationToken);
                status = VerificationStatus;
            }
            else if (path.StartsWith("/Users/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ExplicitUsers);
                body = "{\"Id\":\"" + path[7..] + "\"}";
            }
            else if (path == "/System/Info") Interlocked.Increment(ref NativeRelays);
            else status = RelayStatus;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
