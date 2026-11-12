using allstarr.Core.Caching;
using System.Net;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class LibraryBackendBrandResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Jellyfin")]
    [InlineData("jElLyFiN")]
    public async Task JellyfinConfiguration_UsesTheKnownBrandWithoutARequest(string? backendType)
    {
        var clock = new Clock();
        var configuration = Configuration(backendType);
        var source = new Source(_ => throw new InvalidOperationException("No request is needed."));
        var handler = new Handler((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            throw new InvalidOperationException("No probe is needed.")));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = new LibraryBackendBrandResolver(source, new Clients(handler), cache, clock, configuration);

        Assert.Equal(new LibraryBackendBrand("jellyfin", "Jellyfin"), await resolver.ResolveAsync(Context(clock)));
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"type\":\"Navidrome\"}}")]
    [InlineData("{\"SUBSONIC-RESPONSE\":{\"STATUS\":\"ok\",\"TYPE\":\"nAvIdRoMe\"}}")]
    public async Task NavidromeType_IsReadCaseInsensitivelyAndUsesTheAuthenticatedPing(string json)
    {
        var clock = new Clock();
        var configuration = Configuration();
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://backend.test/music/rest/ping.view", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-session", request.Headers.GetValues("X-Session").Single());
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("u=listener&t=fixture-token&s=fixture-salt&v=1.16.1&c=allstarr&f=json",
                await request.Content.ReadAsStringAsync());
            return Json(json);
        });
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock, configuration);

        Assert.Equal(new LibraryBackendBrand("navidrome", "Navidrome"), await resolver.ResolveAsync(Context(clock)));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"type\":\"Other Server\"}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"serverVersion\":\"0.54.1\"}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"serverVersion\":\"NavidromeCompatible\"}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"type\":[],\"serverVersion\":42}}")]
    public async Task UnidentifiedSubsonicServers_KeepTheGenericBrand(string json)
    {
        var clock = new Clock();
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(new Handler(_ => Json(json)), cache, clock);

        Assert.Equal(new LibraryBackendBrand("subsonic", "Subsonic server"), await resolver.ResolveAsync(Context(clock)));
    }

    [Theory]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"serverVersion\":\"Navidrome 0.54.1\"}}")]
    [InlineData("{\"subsonic-response\":{\"status\":\"ok\",\"SERVERVERSION\":\"0.54.1 (NAVIDROME)\"}}")]
    public async Task ServerVersion_CanIdentifyNavidrome(string json)
    {
        var clock = new Clock();
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(new Handler(_ => Json(json)), cache, clock);

        Assert.Equal(new LibraryBackendBrand("navidrome", "Navidrome"), await resolver.ResolveAsync(Context(clock)));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthenticationFailureEnvelope_CanIdentifyTheServerAndExpiresSoon(HttpStatusCode statusCode)
    {
        var clock = new Clock();
        var handler = new Handler(_ => Json(
            "{\"subsonic-response\":{\"status\":\"failed\",\"type\":\"Navidrome\",\"error\":{\"code\":40}}}", statusCode));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock);

        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(1, handler.Calls);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SuccessfulBrand_IsCachedAcrossViewersAndExpiresWithinAnHour()
    {
        var clock = new Clock();
        var handler = new Handler(_ => Json("{\"subsonic-response\":{\"status\":\"ok\",\"type\":\"Navidrome\"}}"));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock);

        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock, user: "another-listener"))).Id);
        Assert.Equal(1, handler.Calls);
        clock.UtcNow = clock.UtcNow.AddMinutes(31);
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Cache_IsIsolatedByConfiguredUrlAndBackendInstance()
    {
        var clock = new Clock();
        var configuration = Configuration();
        var handler = new Handler(request => request.RequestUri!.Host == "backend.test"
            ? Json("{\"subsonic-response\":{\"status\":\"ok\",\"type\":\"Navidrome\"}}")
            : Json("{\"subsonic-response\":{\"status\":\"ok\",\"type\":\"Other\"}}"));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock, configuration);

        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock, backend: "secondary"))).Id);
        Assert.Equal(2, handler.Calls);
        configuration["Subsonic:Url"] = "https://another.test/music";
        Assert.Equal("subsonic", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(3, handler.Calls);
        configuration["Subsonic:Url"] = "https://backend.test/music";
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("json")]
    [InlineData("envelope")]
    [InlineData("status")]
    public async Task FailedProbes_FallBackBrieflyThenRecover(string failure)
    {
        var clock = new Clock();
        var failed = true;
        var handler = new Handler(_ =>
        {
            if (!failed) return Json("{\"subsonic-response\":{\"status\":\"ok\",\"type\":\"Navidrome\"}}");
            return failure switch
            {
                "transport" => throw new HttpRequestException("Backend unavailable."),
                "json" => Json("invalid json"),
                "envelope" => Json("{}"),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            };
        });
        using var cache = new MemoryApplicationCache(clock);
        var resolver = Resolver(handler, cache, clock);

        Assert.Equal("subsonic", (await resolver.ResolveAsync(Context(clock))).Id);
        failed = false;
        Assert.Equal("subsonic", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(1, handler.Calls);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task MissingAuthenticatedRequest_DoesNotReadOrWriteTheBrandCache()
    {
        var clock = new Clock();
        var available = false;
        var configuration = Configuration();
        var source = new Source(_ => available ? Request(configuration) : null);
        var handler = new Handler(_ => Json("{\"subsonic-response\":{\"status\":\"ok\",\"type\":\"Navidrome\"}}"));
        using var cache = new MemoryApplicationCache(clock);
        var resolver = new LibraryBackendBrandResolver(source, new Clients(handler), cache, clock, configuration);

        Assert.Equal("subsonic", (await resolver.ResolveAsync(Context(clock))).Id);
        available = true;
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(1, handler.Calls);
        available = false;
        Assert.Equal("subsonic", (await resolver.ResolveAsync(Context(clock))).Id);
        available = true;
        Assert.Equal("navidrome", (await resolver.ResolveAsync(Context(clock))).Id);
        Assert.Equal(1, handler.Calls);
    }

    private static IConfiguration Configuration(string? backendType = "Subsonic") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:Type"] = backendType,
            ["Subsonic:Url"] = "https://backend.test/music"
        }).Build();

    private static LibraryBackendBrandResolver Resolver(Handler handler, IApplicationCache cache, Clock clock,
        IConfiguration? configuration = null)
    {
        configuration ??= Configuration();
        return new(new Source(_ => Request(configuration)), new Clients(handler), cache, clock, configuration);
    }

    private static HttpRequestMessage Request(IConfiguration configuration)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, configuration["Subsonic:Url"]!.TrimEnd('/') + "/rest/getMusicFolders.view")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["u"] = "listener",
                ["t"] = "fixture-token",
                ["s"] = "fixture-salt",
                ["v"] = "1.16.1",
                ["c"] = "allstarr",
                ["f"] = "json"
            })
        };
        request.Headers.Add("X-Session", "fixture-session");
        return request;
    }

    private static ProtocolExecutionContext Context(Clock clock, string backend = "primary", string user = "listener") =>
        new(ProtocolKind.Subsonic, backend, user,
            new AllstarrPrincipal(Guid.CreateVersion7(), "subsonic", backend, user, user, false),
            "backend-brand-test", clock.UtcNow.AddMinutes(5), default);

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Source(Func<ProtocolExecutionContext, HttpRequestMessage?> request) : IBackendLibraryPermissionSource
    {
        public int Calls { get; private set; }
        public Task<HttpRequestMessage?> CreateRequestAsync(ProtocolExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(request(context));
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(LibraryBackendBrandResolver.HttpClientName, name);
            return new(handler, disposeHandler: false);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _response;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : this(request => Task.FromResult(response(request))) { }
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) => _response = response;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return _response(request);
        }
    }
}
