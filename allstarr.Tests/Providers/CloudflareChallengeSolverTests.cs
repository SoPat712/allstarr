using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class CloudflareChallengeSolverTests
{
    private const string Target = "https://lyrics-api.binimum.org/?isrc=private-query";
    private readonly Clock _clock = new();

    [Theory]
    [InlineData(403, "cloudflare", "", "<title>Just a moment...</title>", "", true)]
    [InlineData(503, "cloudflare-nginx", "", "<title>Attention Required! | Cloudflare</title>", "", true)]
    [InlineData(429, "cloudflare", "challenge", "", "", true)]
    [InlineData(307, "cloudflare", "", "", "/cdn-cgi/challenge-platform/check", true)]
    [InlineData(307, "", "", "/cdn-cgi/challenge-platform/check", "", true)]
    [InlineData(403, "cloudflare", "", "ordinary denial", "", false)]
    [InlineData(429, "", "", "rate limit", "", false)]
    [InlineData(200, "cloudflare", "challenge", "", "", false)]
    public async Task DetectsChallengesWithoutMistakingOrdinaryFailures(int status, string server, string mitigated,
        string body, string location, bool expected)
    {
        using var response = Reply(body, status);
        if (server.Length > 0) response.Headers.TryAddWithoutValidation("Server", server);
        if (mitigated.Length > 0) response.Headers.TryAddWithoutValidation("cf-mitigated", mitigated);
        if (location.Length > 0) response.Headers.Location = new(location, UriKind.Relative);
        Assert.Equal(expected, await CloudflareChallengeSolver.IsChallengeAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task SolvesExactUrlThenReusesHostCookiesAndUserAgentUntilExpiry()
    {
        var calls = 0;
        var solver = Solver(new Handler(async request =>
        {
            calls++;
            Assert.Equal("http://solver:8191/v1", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(3, json.RootElement.EnumerateObject().Count());
            Assert.Equal("request.get", json.RootElement.GetProperty("cmd").GetString());
            Assert.Equal(Target, json.RootElement.GetProperty("url").GetString());
            Assert.Equal(60000, json.RootElement.GetProperty("maxTimeout").GetInt32());
            return Solution();
        }));
        var normal = 0;
        using var client = Client(solver, request =>
        {
            normal++;
            if (!request.Headers.Contains("Cookie")) return Challenge();
            Assert.Equal("cf_clearance=clearance-secret", request.Headers.GetValues("Cookie").Single());
            Assert.Equal("FixtureBrowser/1.0", request.Headers.UserAgent.ToString());
            return Reply("words");
        });
        Assert.Equal("words", await client.GetStringAsync(Target));
        Assert.Equal("words", await client.GetStringAsync(Target));
        Assert.Equal(1, calls);
        _clock.Now = _clock.Now.AddMinutes(2);
        Assert.Equal("words", await client.GetStringAsync(Target));
        Assert.Equal(2, calls);
        Assert.Equal(5, normal);
    }

    [Fact]
    public async Task StillChallengedGetUsesOnlySuccessfulTextSolutionForExactUrl()
    {
        var solver = Solver(new Handler(_ => Task.FromResult(Solution("{\"results\":[]}"))));
        using var client = Client(solver, _ => Challenge());
        Assert.Equal("{\"results\":[]}", await client.GetStringAsync(Target));
    }

    [Theory]
    [InlineData("https://other.test/")]
    [InlineData("https://lyrics-api.binimum.org/?isrc=other")]
    public async Task RejectsSolverSolutionForDifferentTarget(string returnedUrl)
    {
        var solver = Solver(new Handler(_ => Task.FromResult(Solution(url: returnedUrl))));
        using var client = Client(solver, _ => Challenge());
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
    }

    [Fact]
    public async Task FailureBackoffPreventsRepeatedSolverCallsAndResumesLater()
    {
        var calls = 0;
        var solver = Solver(new Handler(_ => { calls++; throw new HttpRequestException("private-query clearance-secret"); }));
        using var client = Client(solver, _ => Challenge());
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
            Assert.DoesNotContain("private-query", error.ToString());
            Assert.DoesNotContain("clearance-secret", error.ToString());
        }
        Assert.Equal(1, calls);
        _clock.Now = _clock.Now.AddMinutes(1);
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentChallengesShareOneSolveButEachRetriesItsOwnRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var solver = Solver(new Handler(async _ => { Interlocked.Increment(ref calls); started.SetResult(); await release.Task; return Solution(); }));
        using var client = Client(solver, request => request.Headers.Contains("Cookie") ? Reply("words") : Challenge());
        var first = client.GetStringAsync(Target);
        await started.Task;
        var second = client.GetStringAsync(Target);
        release.SetResult();
        Assert.Equal(new[] { "words", "words" }, await Task.WhenAll(first, second));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisabledSolverHasClearHostChosenMessageAndOptOutHostsAreUntouched()
    {
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => throw new InvalidOperationException())),
            _ => Task.FromResult(new ChallengeSolverOptions("", 60)), _clock);
        using var client = Client(solver, _ => Challenge());
        var error = await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        Assert.Equal("blocked-by-challenge", ProviderError.BlockedByChallenge().Code);
        Assert.Equal("This source is behind a Cloudflare check. Add a FlareSolverr or Byparr URL in Settings to use it.", error.Message);
        using var untouched = await client.GetAsync("https://ordinary.test/");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, untouched.StatusCode);
    }

    [Theory]
    [InlineData("{\"status\":\"ok\",\"version\":\"3.4.0\"}", true)]
    [InlineData("{\"msg\":\"Byparr is working!\",\"version\":\"2.0\"}", true)]
    [InlineData("{\"status\":\"error\",\"message\":\"private-body\"}", false)]
    [InlineData("not json private-body", false)]
    public async Task HealthReportsOnlySafeStatusAndVersion(string body, bool ok)
    {
        var solver = Solver(new Handler(request =>
        {
            Assert.Equal("http://solver:8191/health", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Reply(body));
        }));
        var result = await solver.TestAsync(new("http://solver:8191", 60), CancellationToken.None);
        Assert.Equal(ok, result.Ok);
        Assert.DoesNotContain("private-body", result.Message);
        if (ok) Assert.Equal("Solver reachable", result.Message);
    }

    [Theory]
    [InlineData(503, "application/json")]
    [InlineData(200, "application/octet-stream")]
    public async Task FallbackRejectsFailedOrBinarySolutions(int status, string contentType)
    {
        var json = JsonSerializer.Serialize(new
        {
            status = "ok",
            solution = new
            {
                url = Target,
                status,
                response = "private-body",
                userAgent = "FixtureBrowser/1.0",
                cookies = Array.Empty<object>(),
                headers = new Dictionary<string, string> { ["content-type"] = contentType }
            }
        });
        using var client = Client(Solver(new Handler(_ => Task.FromResult(Reply(json)))), _ => Challenge());
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
    }

    [Fact]
    public async Task ClearanceIsNotSharedWithAnotherHostOrAfterDisablingSolver()
    {
        var enabled = true;
        var calls = 0;
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Solution()); })),
            _ => Task.FromResult(new ChallengeSolverOptions(enabled ? "http://solver:8191" : "", 60)), _clock);
        using var client = new HttpClient(new CloudflareChallengeHandler(solver, ["lyrics-api.binimum.org", "lyrics-storage.binimum.org"])
        {
            InnerHandler = new Handler(request =>
            {
                if (request.RequestUri!.Host == "lyrics-storage.binimum.org" || !enabled)
                    Assert.False(request.Headers.Contains("Cookie"));
                return Task.FromResult(request.RequestUri.Host == "lyrics-storage.binimum.org" ? Reply("storage") : Challenge());
            })
        });
        Assert.Equal("words", await client.GetStringAsync(Target));
        Assert.Equal("storage", await client.GetStringAsync("https://lyrics-storage.binimum.org/fixture.ttml"));
        enabled = false;
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NamedClientsSuppressQueryCookieAndSolverBodyLogging()
    {
        var messages = new List<string>();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new LogProvider(messages)));
        services.AddSingleton<allstarr.Core.Settings.IDurableRuntimeSettings>(Moq.Mock.Of<allstarr.Core.Settings.IDurableRuntimeSettings>());
        allstarr.Core.Providers.Lyrics.BuiltInLyricsCapabilityRegistration.AddBuiltInLyricsCapabilities(services);
        foreach (var name in new[] { CloudflareChallengeSolver.HttpClientName,
            allstarr.Core.Providers.Lyrics.BiniLyricsCapabilityAdapter.HttpClientName,
            allstarr.Core.Providers.Lyrics.LyricsPlusCapabilityAdapter.HttpClientName })
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new Handler(_ => Task.FromResult(Reply("private-body"))));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        // A non-opted host avoids resolving settings while exercising the named pipeline's logging configuration.
        foreach (var name in new[] { CloudflareChallengeSolver.HttpClientName,
            allstarr.Core.Providers.Lyrics.BiniLyricsCapabilityAdapter.HttpClientName,
            allstarr.Core.Providers.Lyrics.LyricsPlusCapabilityAdapter.HttpClientName })
        {
            using var client = factory.CreateClient(name);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://fixture.test/?secret=private-query");
            request.Headers.TryAddWithoutValidation("Cookie", "cf_clearance=clearance-secret");
            await client.SendAsync(request);
        }
        Assert.DoesNotContain(messages, message => message.Contains("private-query") || message.Contains("clearance-secret") || message.Contains("private-body"));
    }

    private sealed class LogProvider(List<string> messages) : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CapturedLog(messages);
        public void Dispose() { }
    }
    private sealed class CapturedLog(List<string> messages) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task ConfiguredTimeoutCancelsSolverAndStartsBackoff()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var solver = new CloudflareChallengeSolver(new HttpClient(new WaitingHandler(() => Interlocked.Increment(ref calls))),
            _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 10)), clock);
        using var client = Client(solver, _ => Challenge());
        var pending = client.GetAsync(Target);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => pending);
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedSolutionIsRejectedWithinTheLyricsBodyLimit()
    {
        using var client = Client(Solver(new Handler(_ => Task.FromResult(Solution(new string('x', 2_000_001))))), _ => Challenge());
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
    }

    private sealed class WaitingHandler(Action started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            started();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private CloudflareChallengeSolver Solver(HttpMessageHandler transport) => new(new HttpClient(transport),
        _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 60)), _clock);
    private static HttpClient Client(CloudflareChallengeSolver solver, Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new CloudflareChallengeHandler(solver, ["lyrics-api.binimum.org"]) { InnerHandler = new Handler(request => Task.FromResult(send(request))) });
    private HttpResponseMessage Solution(string body = "words", string url = Target) => Reply(JsonSerializer.Serialize(new
    {
        status = "ok",
        solution = new
        {
            url,
            status = 200,
            response = body,
            headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            userAgent = "FixtureBrowser/1.0",
            cookies = new[] { new { name = "cf_clearance", value = "clearance-secret", domain = ".binimum.org", path = "/", expires = _clock.Now.AddMinutes(1).ToUnixTimeSeconds(), secure = true } }
        }
    }));
    private static HttpResponseMessage Challenge()
    {
        var response = Reply("challenge", 307);
        response.Headers.Server.ParseAdd("cloudflare");
        response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
        return response;
    }
    private static HttpResponseMessage Reply(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
