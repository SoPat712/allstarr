using System.Diagnostics;
using System.Net;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class SubsonicAuthVerificationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RepeatedRequestsReuseVerificationAndResolvedPrincipalAcrossScopes()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var contexts = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        contexts.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AllstarrDbContext(database.Options));
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var resolver = new BackendIdentityResolver(contexts.Object, state, new IdentityOptions(), new SystemPlatformClock());
        using var fixture = new Fixture(resolver);
        AllstarrPrincipal? first = null;
        for (var index = 0; index < 10; index++)
        {
            var context = await fixture.Execute();
            Assert.Null(context.Result);
            Assert.Equal("listener", context.HttpContext.Items[SubsonicAuthFilter.BackendPrincipalNameItemKey]);
            var principal = Assert.IsType<AllstarrPrincipal>(context.HttpContext.Items[BackendIdentityResolver.HttpContextPrincipalItemKey]);
            first ??= principal;
            Assert.Same(first, principal);
        }
        Assert.Equal(1, fixture.Backend.Pings);
        contexts.Verify(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApiKeyRequestsReuseTokenInfoAndPrincipalName()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 5; index++)
        {
            var context = await fixture.Execute("?apiKey=private-key&f=json");
            Assert.Null(context.Result);
            Assert.Equal("key-listener", context.HttpContext.Items[SubsonicAuthFilter.BackendPrincipalNameItemKey]);
        }
        Assert.Equal(1, fixture.Backend.Pings);
        Assert.Equal(1, fixture.Backend.TokenInfos);
    }

    [Fact]
    public async Task ConcurrentRequestsShareSuccessfulVerification()
    {
        using var fixture = new Fixture();
        fixture.Backend.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 12).Select(_ => fixture.Execute()).ToArray();
        await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, fixture.Backend.Pings);
        fixture.Backend.Release.SetResult();
        Assert.All(await Task.WhenAll(requests), context => Assert.Null(context.Result));
        Assert.Equal(1, fixture.Backend.Pings);
    }

    [Fact]
    public async Task ExpiryIsFixedFromSuccessfulVerification()
    {
        using var fixture = new Fixture();
        await fixture.Execute();
        fixture.Clock.Advance(TimeSpan.FromSeconds(119));
        await fixture.Execute();
        Assert.Equal(1, fixture.Backend.Pings);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Execute();
        Assert.Equal(2, fixture.Backend.Pings);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("protocol")]
    [InlineData("invalid")]
    [InlineData("missing_principal")]
    public async Task FailedVerificationsAreNeverReused(string failure)
    {
        using var fixture = new Fixture();
        fixture.Backend.PingStatus = failure == "http" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
        fixture.Backend.PingBody = failure switch
        {
            "protocol" => """{"subsonic-response":{"status":"failed","error":{"code":40}}}""",
            "invalid" => "invalid",
            _ => Backend.Success
        };
        fixture.Backend.TokenInfoBody = """{"subsonic-response":{"status":"ok","tokenInfo":{}}}""";
        var query = failure == "missing_principal" ? "?apiKey=private-key&f=json" : Fixture.DefaultQuery;
        Assert.NotNull((await fixture.Execute(query)).Result);
        Assert.NotNull((await fixture.Execute(query)).Result);
        Assert.Equal(2, fixture.Backend.Pings);
    }

    [Theory]
    [InlineData("http", false)]
    [InlineData("forbidden", false)]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("http", true)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    public async Task UpstreamAuthenticationFailureEvictsVerification(string failure, bool stream)
    {
        using var fixture = new Fixture();
        await fixture.Execute();
        fixture.Backend.RelayStatus = failure switch
        {
            "http" => HttpStatusCode.Unauthorized,
            "forbidden" => HttpStatusCode.Forbidden,
            _ => HttpStatusCode.OK
        };
        fixture.Backend.RelayBody = failure == "xml"
            ? """<subsonic-response xmlns="http://subsonic.org/restapi" status="failed"><error code="40" message="Rejected"/></subsonic-response>"""
            : """{"subsonic-response":{"status":"failed","error":{"code":44}}}""";
        fixture.Backend.RelayContentType = failure == "xml" ? "text/xml" : "application/json";
        var parameters = await new SubsonicRequestParser().ExtractAllParametersAsync(Fixture.Context().HttpContext.Request);
        if (stream)
        {
            var result = await fixture.Proxy().RelayStreamAsync(parameters, default);
            if (result is FileStreamResult file)
            {
                using var reader = new StreamReader(file.FileStream);
                Assert.Equal(fixture.Backend.RelayBody, await reader.ReadToEndAsync());
            }
        }
        else await fixture.Proxy().RelayRawAsync("rest/getSong", parameters);
        await fixture.Execute();
        Assert.Equal(2, fixture.Backend.Pings);
    }

    [Fact]
    public async Task NonAuthenticationProtocolFailureKeepsVerification()
    {
        using var fixture = new Fixture();
        await fixture.Execute();
        fixture.Backend.RelayBody = """{"subsonic-response":{"status":"failed","error":{"code":70}}}""";
        var parameters = SubsonicRequestParameters.FromDictionary(new Dictionary<string, string>
        { ["u"] = "listener", ["p"] = "private-password", ["f"] = "json" });
        await fixture.Proxy().RelayRawAsync("rest/getSong", parameters);
        await fixture.Execute();
        Assert.Equal(1, fixture.Backend.Pings);
    }

    [Fact]
    public async Task CredentialsMethodBodyAndRepeatedValuesRemainIsolated()
    {
        using var fixture = new Fixture();
        await fixture.Execute();
        await fixture.Execute("?u=listener&p=other-password&f=json");
        await fixture.Execute("?u=other-listener&p=private-password&f=json");
        await fixture.Execute("?u=listener&p=private-password&p=other-password&f=json");
        await fixture.Execute("?u=listener&p=other-password&p=private-password&f=json");
        await fixture.Execute(Fixture.DefaultQuery, "POST");
        await fixture.Execute("?f=json", "POST", "u=listener&p=private-password", "application/x-www-form-urlencoded");
        await fixture.Execute("?f=json", "POST", "u=listener&p=other-password", "application/x-www-form-urlencoded");
        await fixture.Execute("?f=json", "POST", """{"u":"listener","p":"private-password"}""", "application/json");
        await fixture.Execute();
        Assert.Equal(9, fixture.Backend.Pings);
        var parameters = await new SubsonicRequestParser().ExtractAllParametersAsync(Fixture.Context().HttpContext.Request);
        var key = SubsonicVerificationCache.CredentialKey(parameters);
        Assert.Equal(64, key.Length);
        Assert.DoesNotContain("private-password", key);
    }

    [Fact]
    public async Task CachedVerificationResolvesPrincipalWhenStorageBecomesReady()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var contexts = new Mock<IDbContextFactory<AllstarrDbContext>>(MockBehavior.Strict);
        contexts.Setup(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AllstarrDbContext(database.Options));
        var state = new DurableStorageState(database.StorageOptions);
        using var fixture = new Fixture(new BackendIdentityResolver(contexts.Object, state, new IdentityOptions(), new SystemPlatformClock()));
        Assert.False((await fixture.Execute()).HttpContext.Items.ContainsKey(BackendIdentityResolver.HttpContextPrincipalItemKey));
        state.Set(DurableStorageReadiness.Ready);
        Assert.IsType<AllstarrPrincipal>((await fixture.Execute()).HttpContext.Items[BackendIdentityResolver.HttpContextPrincipalItemKey]);
        Assert.Equal(1, fixture.Backend.Pings);
        contexts.Verify(item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DelayedBackendReportsRepeatedVerificationCost()
    {
        const int requests = 8;
        using var fixture = new Fixture();
        fixture.Backend.Delay = TimeSpan.FromMilliseconds(20);
        var repeatedSamples = new List<double>();
        var cachedSamples = new List<double>();
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < requests; index++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            var request = Stopwatch.StartNew();
            await fixture.Execute("?apiKey=private-key&f=json");
            repeatedSamples.Add(request.Elapsed.TotalMilliseconds);
        }
        var before = elapsed.Elapsed;
        var beforePings = fixture.Backend.Pings;
        var beforeTokenInfos = fixture.Backend.TokenInfos;
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.Execute("?apiKey=private-key&f=json");
        var warmPings = fixture.Backend.Pings;
        var warmTokenInfos = fixture.Backend.TokenInfos;
        elapsed.Restart();
        for (var index = 0; index < requests; index++)
        {
            var request = Stopwatch.StartNew();
            await fixture.Execute("?apiKey=private-key&f=json");
            cachedSamples.Add(request.Elapsed.TotalMilliseconds);
        }
        var after = elapsed.Elapsed;
        static double Median(List<double> samples) => samples.Order().Skip(samples.Count / 2 - 1).Take(2).Average();
        var measurement = $"Subsonic verification: requests={requests}; delayed backend=20ms/call; repeated verification={before.TotalMilliseconds:F1}ms, median={Median(repeatedSamples):F3}ms/request ({beforePings} ping, {beforeTokenInfos} tokenInfo); warm cached={after.TotalMilliseconds:F1}ms, median={Median(cachedSamples):F3}ms/request ({fixture.Backend.Pings - warmPings} ping, {fixture.Backend.TokenInfos - warmTokenInfos} tokenInfo)";
        output.WriteLine(measurement);
        Console.WriteLine(measurement);
        Assert.Equal(requests, beforePings);
        Assert.Equal(requests, beforeTokenInfos);
        Assert.Equal(requests + 1, fixture.Backend.Pings);
        Assert.Equal(requests + 1, fixture.Backend.TokenInfos);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string DefaultQuery = "?u=listener&p=private-password&f=json";
        internal readonly Backend Backend = new();
        internal readonly ManualTimeProvider Clock = new();
        private readonly SubsonicVerificationCache cache;
        private readonly BackendIdentityResolver resolver;
        private readonly HttpClient client;
        private readonly IHttpClientFactory factory;

        internal Fixture(BackendIdentityResolver? resolver = null)
        {
            cache = new(Clock);
            this.resolver = resolver ?? new BackendIdentityResolver(Mock.Of<IDbContextFactory<AllstarrDbContext>>(),
                new DurableStorageState(new StorageOptions()), new IdentityOptions(), new SystemPlatformClock());
            client = new HttpClient(Backend);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
            factory = clients.Object;
        }

        internal SubsonicProxyService Proxy() => new(factory,
            Options.Create(new SubsonicSettings { Url = "http://backend.test" }),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, verificationCache: cache);

        internal async Task<ResourceExecutingContext> Execute(string query = DefaultQuery, string method = "GET",
            string? body = null, string? contentType = null)
        {
            var context = Context(query, method, body, contentType);
            var filter = new SubsonicAuthFilter(new SubsonicRequestParser(), Proxy(), new SubsonicResponseBuilder(),
                resolver, NullLogger<SubsonicAuthFilter>.Instance);
            await filter.OnResourceExecutionAsync(context,
                () => Task.FromResult(new ResourceExecutedContext(context, [])));
            return context;
        }

        internal static ResourceExecutingContext Context(string query = DefaultQuery, string method = "GET",
            string? body = null, string? contentType = null)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = "/rest/getSong";
            http.Request.Method = method;
            http.Request.QueryString = new QueryString(query);
            http.Request.ContentType = contentType;
            if (body != null)
            {
                http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
                http.Request.ContentLength = http.Request.Body.Length;
            }
            return new ResourceExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], []);
        }

        public void Dispose() { cache.Dispose(); client.Dispose(); }
    }

    private sealed class Backend : HttpMessageHandler
    {
        internal const string Success = """{"subsonic-response":{"status":"ok"}}""";
        internal int Pings;
        internal int TokenInfos;
        internal HttpStatusCode PingStatus = HttpStatusCode.OK;
        internal string PingBody = Success;
        internal string TokenInfoBody = """{"subsonic-response":{"status":"ok","tokenInfo":{"username":"key-listener"}}}""";
        internal HttpStatusCode RelayStatus = HttpStatusCode.OK;
        internal string RelayBody = Success;
        internal string RelayContentType = "application/json";
        internal TimeSpan Delay;
        internal TaskCompletionSource? Started;
        internal TaskCompletionSource? Release;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.AbsolutePath;
            var body = RelayBody;
            var status = RelayStatus;
            var contentType = RelayContentType;
            if (endpoint == "/rest/ping.view")
            {
                Interlocked.Increment(ref Pings);
                Started?.TrySetResult();
                if (Release != null) await Release.Task.WaitAsync(cancellationToken);
                body = PingBody;
                status = PingStatus;
                contentType = "application/json";
            }
            else if (endpoint == "/rest/tokenInfo.view")
            {
                Interlocked.Increment(ref TokenInfos);
                body = TokenInfoBody;
                contentType = "application/json";
            }
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        }
    }
}
