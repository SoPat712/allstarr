using allstarr.Core.Providers.Lyrics;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using Microsoft.Extensions.Configuration;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

public sealed class CloudflareChallengeSolverTests
{
    private const string Target = "https://lyrics.example.test/?isrc=private-query";
    private readonly Clock _clock = new();

    [Fact]
    public async Task AdmissionRequiresRecentRealDeliveryAndExactCurrentConfiguration()
    {
        var options = new ChallengeSolverOptions("http://solver:8191", 60);
        var revision = 1L;
        var solverCalls = 0;
        ChallengeSolverConfiguration Configuration() => new(options, LyricsConfigurationId.From(options.Url + ":" + options.TimeoutSeconds, revision));
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ =>
        { solverCalls++; return Task.FromResult(Solution()); })), _ => Task.FromResult(options), _clock)
        { ConfigurationReader = _ => Task.FromResult(Configuration()), AdmissionTtl = TimeSpan.FromSeconds(10) };
        var before = await solver.CaptureConfigurationAsync(default);
        Assert.False(solver.IsReady("lyrics.example.test", before));
        Assert.Equal(0, solverCalls);
        Assert.True((await solver.TestAsync(options, default)).Ok);
        Assert.False(solver.IsReady("lyrics.example.test", before));
        var originCalls = 0;
        using var client = Client(solver, request =>
        { originCalls++; return request.Headers.Contains("Cookie") ? Reply("Words") : Challenge(); });
        using (var response = await client.GetAsync(Target)) Assert.True(response.IsSuccessStatusCode);
        Assert.True(solver.IsReady("lyrics.example.test", before));
        _clock.Now += TimeSpan.FromSeconds(11);
        Assert.False(solver.IsReady("lyrics.example.test", before));
        revision++;
        var changed = await solver.CaptureConfigurationAsync(default);
        Assert.NotEqual(before.ConfigurationId, changed.ConfigurationId);
        Assert.False(solver.IsReady("lyrics.example.test", changed));
        var callsBefore = originCalls;
        using var stale = new HttpRequestMessage(HttpMethod.Get, Target);
        stale.Options.Set(CloudflareChallengeSolver.ExpectedConfiguration, before);
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.SendAsync(stale));
        Assert.Equal(callsBefore, originCalls);
    }

    [Fact]
    public async Task FailedCookieDeliveryRevokesSpeculativeSolverReadiness()
    {
        var failed = false;
        var solver = Solver(new Handler(_ => Task.FromResult(Solution())));
        using var client = Client(solver, request => request.Headers.Contains("Cookie")
            ? Reply("Words", failed ? 503 : 200) : Challenge());
        using (var first = await client.GetAsync(Target)) Assert.True(first.IsSuccessStatusCode);
        var configuration = await solver.CaptureConfigurationAsync(default);
        Assert.True(solver.IsReady("lyrics.example.test", configuration));
        failed = true;
        using (var second = await client.GetAsync(Target)) Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.False(solver.IsReady("lyrics.example.test", configuration));
    }

    [Theory]
    [InlineData("initial", "read")]
    [InlineData("initial", "cancel")]
    [InlineData("initial", "oversized")]
    [InlineData("fresh", "read")]
    [InlineData("fresh", "cancel")]
    [InlineData("fresh", "oversized")]
    [InlineData("shared", "read")]
    [InlineData("shared", "cancel")]
    [InlineData("shared", "oversized")]
    public async Task FailedBodyInspectionDisposesResponse(string path, string failure)
    {
        var content = new FailedInspectionContent(failure);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var solver = Solver(new Handler(async _ =>
        {
            started.SetResult();
            if (path == "shared") await release.Task;
            return Solution();
        }));
        var retries = 0;
        using var client = Client(solver, request =>
        {
            if (path != "initial" && !request.Headers.Contains("Cookie")) return Challenge();
            if (path == "shared" && ++retries == 1) return Reply("words");
            return new(HttpStatusCode.OK) { Content = content };
        });
        var first = client.GetAsync(Target, HttpCompletionOption.ResponseHeadersRead);
        if (path == "shared")
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = client.GetAsync(Target, HttpCompletionOption.ResponseHeadersRead);
            release.SetResult();
            using var response = await first;
            await Assert.ThrowsAnyAsync<Exception>(() => second);
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => first);
        Assert.True(content.Disposed);
    }

    private sealed class FailedInspectionContent(string failure) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            if (failure == "cancel") throw new OperationCanceledException();
            if (failure == "read") throw new IOException("Interrupted response body.");
            await stream.WriteAsync(new byte[CloudflareChallengeSolver.MaximumBytes + 1]);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Theory]
    [InlineData(403, "cloudflare", "", "<title>Just a moment...</title>", "", true)]
    [InlineData(503, "cloudflare-nginx", "", "<title>Attention Required! | Cloudflare</title>", "", true)]
    [InlineData(429, "cloudflare", "challenge", "", "", true)]
    [InlineData(307, "cloudflare", "", "", "/cdn-cgi/challenge-platform/check", true)]
    [InlineData(307, "", "", "/cdn-cgi/challenge-platform/check", "", true)]
    [InlineData(403, "cloudflare", "", "ordinary denial", "", false)]
    [InlineData(429, "", "", "rate limit", "", false)]
    [InlineData(200, "cloudflare", "challenge", "", "", true)]
    [InlineData(200, "cloudflare", "", "<title>Just a moment...</title>", "", true)]
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
    [InlineData("http://lyrics.example.test/")]
    [InlineData("https://lyrics.example.test:8443/")]
    [InlineData("https://user:secret@lyrics.example.test/")]
    [InlineData("https://storage.example.test/fixture.ttml")]
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
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Solution("{\"results\":[]}")); })),
            _ => Task.FromResult(new ChallengeSolverOptions(enabled ? "http://solver:8191" : "", 60)), _clock);
        using var client = new HttpClient(new CloudflareChallengeHandler(solver, ["lyrics.example.test", "storage.example.test"])
        {
            InnerHandler = new Handler(request =>
            {
                if (request.RequestUri!.Host == "storage.example.test" || !enabled)
                    Assert.False(request.Headers.Contains("Cookie"));
                return Task.FromResult(request.RequestUri.Host == "storage.example.test" ? Reply("storage") : Challenge());
            })
        });
        Assert.Equal("{\"results\":[]}", await client.GetStringAsync(Target));
        Assert.Equal("storage", await client.GetStringAsync("https://storage.example.test/fixture.ttml"));
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
            allstarr.Core.Providers.Lyrics.LyricsPlusCapabilityAdapter.HttpClientName })
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new Handler(_ => Task.FromResult(Reply("private-body"))));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        // A non-opted host avoids resolving settings while exercising the named pipeline's logging configuration.
        foreach (var name in new[] { CloudflareChallengeSolver.HttpClientName,
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

    public static IEnumerable<object[]> RenderedBodies() =>
        JsonSerializer.Deserialize<string[][]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "ChallengeSolver", "rendered-responses.json")))!.Select(row => row.Cast<object>().ToArray());

    [Theory]
    [MemberData(nameof(RenderedBodies))]
    public async Task ValidSolverBodyDeliversExpectedContentBeforeCookieRetry(string body, string contentType, string expected, string accept)
    {
        var normal = 0;
        using var client = Client(Solver(new Handler(_ => Task.FromResult(Solution(body, contentType: contentType)))), _ => { normal++; return Challenge(); });
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync(Target);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal(accept, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, normal);
    }

    [Theory]
    [InlineData("<html><head></head><body><pre>{invalid}</pre></body></html>", "application/json")]
    [InlineData("<html><head></head><body><pre>not xml</pre></body></html>", "application/ttml+xml")]
    [InlineData("<html><body>ordinary page</body></html>", "text/plain")]
    [InlineData("<tt><body></tt>", "application/ttml+xml")]
    [InlineData("<root/>", "application/ttml+xml")]
    [InlineData("<!DOCTYPE tt [<!ENTITY secret SYSTEM 'file:///private-fixture'>]><tt>&secret;</tt>", "application/ttml+xml")]
    [InlineData("<html><head><title>Just a moment...</title></head><body><pre>{\"results\":[]}</pre></body></html>", "application/json")]
    [InlineData("<html><body><script src='/cdn-cgi/challenge-platform/check'></script></body></html>", "text/plain")]
    [InlineData("<html><body><div id='cf-challenge-running'></div></body></html>", "text/plain")]
    public async Task InvalidOrStillChallengedBodyStartsBackoff(string body, string accept)
    {
        var calls = 0;
        using var client = Client(Solver(new Handler(_ => { calls++; return Task.FromResult(Solution(body)); })), _ => Challenge());
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("https://lyrics.example.test/match?track=Espresso&artist=Sabrina%20Carpenter", true)]
    [InlineData("https://lyrics.example.test/match/?track=Espresso&artist=Sabrina%20Carpenter", true)]
    [InlineData("https://lyrics.example.test/match?artist=Sabrina%20Carpenter&track=Espresso", true)]
    [InlineData("https://lyrics.example.test/match?track=Espresso&artist=Sabrina+Carpenter&__cf_chl_tk=challenge-secret", true)]
    [InlineData("https://lyrics.example.test/match?__cf_chl_rt_tk=challenge-secret&artist=Sabrina%20Carpenter&track=Espresso", true)]
    [InlineData("https://LYRICS.example.test:443/match?track=Espresso&artist=Sabrina%20Carpenter#result", true)]
    [InlineData("http://lyrics.example.test/match?track=Espresso&artist=Sabrina%20Carpenter", true)]
    [InlineData("http://lyrics.example.test:80/match/?artist=Sabrina%20Carpenter&track=Espresso", true)]
    [InlineData("https://other.test/match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test.other.test/match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test:8443/match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("http://lyrics.example.test:443/match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("ftp://lyrics.example.test/match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://user:credential-secret@lyrics.example.test/match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test/cdn-cgi/challenge-platform/check?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test/other?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test/Match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test/match?track=Other&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test/match?track=Espresso", false)]
    [InlineData("https://lyrics.example.test/match?track=Espresso&artist=Sabrina%20Carpenter&extra=value", false)]
    [InlineData("https://lyrics.example.test/match?Track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("https://lyrics.example.test/match?track=Espresso&track=Espresso&artist=Sabrina%20Carpenter", false)]
    [InlineData("/match?track=Espresso&artist=Sabrina%20Carpenter", false)]
    public async Task SolutionUrlMustMatchRequestedResourceAfterNormalization(string returnedUrl, bool accepted)
    {
        const string target = "https://lyrics.example.test/match?track=Espresso&artist=Sabrina%20Carpenter";
        var requests = 0;
        using var client = Client(Solver(new Handler(_ => Task.FromResult(Solution("{}", returnedUrl)))),
            _ => { requests++; return Challenge(); });
        if (accepted) Assert.Equal("{}", await client.GetStringAsync(target));
        else await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(target));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("https://lyrics.example.test/match?tag=b&tag=a")]
    [InlineData("https://lyrics.example.test/match/?tag=a&__cf_chl_tk=challenge-secret&tag=b")]
    public async Task QueryNormalizationPreservesRepeatedValuesAndRemovesChallengeParametersFromBothUrls(string returnedUrl)
    {
        using var client = Client(Solver(new Handler(_ => Task.FromResult(Solution("{}", returnedUrl)))), _ => Challenge());
        Assert.Equal("{}", await client.GetStringAsync("https://lyrics.example.test/match?tag=a&tag=b&__cf_chl_rt_tk=request-secret"));
    }

    [Theory]
    [InlineData("https://lyrics.example.test/cdn-cgi/challenge-platform/check")]
    [InlineData("https://lyrics.example.test/cdn-cgi")]
    [InlineData("https://lyrics.example.test/CDN-CGI/check")]
    public async Task ChallengePathIsRejectedEvenWhenItMatchesTheRequest(string target)
    {
        using var client = Client(Solver(new Handler(_ => Task.FromResult(Solution("{}", target)))), _ => Challenge());
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(target));
    }

    [Theory]
    [InlineData("https://LYRICS.example.test:443/?isrc=private-query#result", "text/html")]
    [InlineData("https://lyrics.example.test/?isrc=private-query&__cf_chl_tk=challenge-secret", "application/octet-stream")]
    [InlineData("http://lyrics.example.test:80/?isrc=private-query", "")]
    public async Task WrappedJsonUsesRequestedFormatAfterNormalizationOrRedirect(string returnedUrl, string contentType)
    {
        const string body = "<html><body><pre>{&quot;lyrics&quot;:&quot;Fixture words&quot;}</pre></body></html>";
        var requests = 0;
        using var client = Client(Solver(new Handler(_ => Task.FromResult(Solution(body, returnedUrl, contentType)))),
            _ => { requests++; return Challenge(); });
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var response = await client.GetAsync(Target);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Fixture words", json.RootElement.GetProperty("lyrics").GetString());
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task WrappedStorageBodyUsesRequestedFormatAfterSameHostRedirect()
    {
        const string target = "https://storage.example.test/fixture.ttml";
        const string ttml = "<tt><body><p begin='1'>A &amp; B</p></body></tt>";
        var wrapped = "<html><body><pre>" + WebUtility.HtmlEncode(ttml) + "</pre></body></html>";
        var requests = 0;
        var solver = Solver(new Handler(_ => Task.FromResult(Solution(wrapped, target + "/", "application/octet-stream"))));
        using var client = new HttpClient(new CloudflareChallengeHandler(solver, ["storage.example.test"])
        {
            InnerHandler = new Handler(_ => { requests++; return Task.FromResult(Challenge()); })
        });
        client.DefaultRequestHeaders.Accept.ParseAdd("application/ttml+xml");
        using var response = await client.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/ttml+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ttml, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("", "text/plain", 200, Target, "application/json", "empty-body")]
    [InlineData("not json", "application/json", 200, Target, "application/json", "unexpected-content")]
    [InlineData("<title>Just a moment...</title>", "text/html", 200, Target, "application/json", "still-challenge")]
    [InlineData("{}", "application/json", 200, "https://other.test/", "application/json", "url-mismatch")]
    [InlineData("{}", "application/json", 503, Target, "application/json", "unexpected-content")]
    [InlineData("large", "text/plain", 200, Target, "text/plain", "oversize")]
    public async Task FailedDeliveryLogsSpecificReasonThroughRedactingLogger(string body, string contentType, int status,
        string returnedUrl, string accept, string reason)
    {
        if (reason == "oversize") body = new string('x', CloudflareChallengeSolver.MaximumBytes + 1);
        using var output = new StringWriter();
        using var logging = LoggerFactory.Create(builder =>
            builder.AddProvider(new RedactingConsoleLoggerProvider(new ConfigurationBuilder().Build(), output, output)));
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => Task.FromResult(Solution(body, returnedUrl, contentType, status)))),
            _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 60)), _clock,
            logging.CreateLogger<CloudflareChallengeSolver>());
        using var client = Client(solver, _ => Challenge());
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        using var rendered = JsonDocument.Parse(output.ToString());
        var fields = rendered.RootElement.GetProperty("fields");
        Assert.Equal(reason, fields.GetProperty("DeliveryFailureReason").GetString());
        Assert.Equal("none", fields.GetProperty("DeliveryPath").GetString());
        Assert.DoesNotContain("private-query", output.ToString());
        Assert.DoesNotContain("clearance-secret", output.ToString());
    }

    [Theory]
    [InlineData("https://lyrics.example.test/?isrc=private-query&__cf_chl_tk=challenge-secret#fragment-secret", "https://lyrics.example.test/", "/", true)]
    [InlineData("http://lyrics.example.test:80/?isrc=private-query", "http://lyrics.example.test/", "/", true)]
    [InlineData("https://other.test:8443/lookup/?isrc=private-query&__cf_chl_tk=challenge-secret", "https://other.test:8443/", "/lookup/", false)]
    [InlineData("https://lyrics.example.test/match?isrc=private-query", "https://lyrics.example.test/", "/match", false)]
    [InlineData("https://lyrics.example.test/?isrc=other-query&__cf_chl_tk=challenge-secret", "https://lyrics.example.test/", "/", false)]
    [InlineData("https://lyrics.example.test/cdn-cgi/check?isrc=private-query", "https://lyrics.example.test/", "/cdn-cgi/check", false)]
    [InlineData("https://user:credential-secret@lyrics.example.test/cdn-cgi/check?isrc=private-query", "https://lyrics.example.test/", "/cdn-cgi/check", false)]
    public async Task SolveLogShowsReturnedOriginAndPathWithoutQueryOrCredentials(string returnedUrl, string origin, string path, bool accepted)
    {
        using var output = new StringWriter();
        using var logging = LoggerFactory.Create(builder =>
            builder.AddProvider(new RedactingConsoleLoggerProvider(new ConfigurationBuilder().Build(), output, output)));
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => Task.FromResult(Solution("{}", returnedUrl)))),
            _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 60)), _clock,
            logging.CreateLogger<CloudflareChallengeSolver>());
        using var client = Client(solver, _ => Challenge());
        if (accepted) Assert.Equal("{}", await client.GetStringAsync(Target));
        else await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        using var rendered = JsonDocument.Parse(output.ToString());
        var fields = rendered.RootElement.GetProperty("fields");
        Assert.Equal(origin, fields.GetProperty("SolutionOrigin").GetString());
        Assert.Equal(path, fields.GetProperty("SolutionPath").GetString());
        Assert.Equal(accepted ? "not-attempted" : "url-mismatch", fields.GetProperty("DeliveryFailureReason").GetString());
        Assert.DoesNotContain("<redacted>", output.ToString());
        Assert.DoesNotContain("private-query", output.ToString());
        Assert.DoesNotContain("other-query", output.ToString());
        Assert.DoesNotContain("challenge-secret", output.ToString());
        Assert.DoesNotContain("credential-secret", output.ToString());
        Assert.DoesNotContain("fragment-secret", output.ToString());
    }

    [Theory]
    [InlineData("{}", "solver-body", "not-attempted")]
    [InlineData("invalid json", "cookies", "False")]
    [InlineData("invalid json", "none", "True")]
    public async Task RenderedSolveLogKeepsClearanceCountAndRetryOutcomeVisible(string body, string path, string retry)
    {
        using var output = new StringWriter();
        using var logging = LoggerFactory.Create(builder =>
            builder.AddProvider(new RedactingConsoleLoggerProvider(new ConfigurationBuilder().Build(), output, output)));
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => Task.FromResult(Solution(body)))),
            _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 60)), _clock,
            logging.CreateLogger<CloudflareChallengeSolver>());
        using var client = Client(solver, request => path == "cookies" && request.Headers.Contains("Cookie") ? Reply("words") : Challenge());
        if (path == "none") await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        else
        {
            using var response = await client.GetAsync(Target);
        }
        using var rendered = JsonDocument.Parse(output.ToString());
        var fields = rendered.RootElement.GetProperty("fields");
        Assert.Equal(1, fields.GetProperty("ClearanceCount").GetInt32());
        Assert.Equal(retry, fields.GetProperty("RetryRechallenged").GetString());
        Assert.Equal(path == "none" ? "unexpected-content" : "not-attempted", fields.GetProperty("DeliveryFailureReason").GetString());
        Assert.DoesNotContain("<redacted>", output.ToString());
        Assert.DoesNotContain("clearance-secret", output.ToString());
        Assert.DoesNotContain("private-query", output.ToString());
    }

    [Fact]
    public async Task BodyForOneRequestIsNeverDeliveredForAnotherRequest()
    {
        var calls = 0;
        using var client = Client(Solver(new Handler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return Solution("{\"value\":" + ++calls + "}", payload.RootElement.GetProperty("url").GetString()!);
        })), _ => Challenge());
        Assert.Equal("{\"value\":1}", await client.GetStringAsync(Target));
        Assert.Equal("{\"value\":2}", await client.GetStringAsync("https://lyrics.example.test/?isrc=another"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedCookieReuseIsAttemptedOnlyOncePerSolve()
    {
        var calls = 0;
        var cookieRetries = 0;
        using var client = Client(Solver(new Handler(_ => { calls++; return Task.FromResult(Solution("{\"results\":[]}")); })), request =>
        {
            if (request.Headers.Contains("Cookie")) cookieRetries++;
            return Challenge();
        });
        for (var i = 0; i < 4; i++) Assert.Equal("{\"results\":[]}", await client.GetStringAsync(Target));
        Assert.Equal(1, cookieRetries);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("http://other-solver:8191", 60)]
    [InlineData("http://solver:8191", 90)]
    public async Task SettingsChangeClearsFailureBackoff(string url, int timeout)
    {
        var configured = new ChallengeSolverOptions("http://solver:8191", 60);
        var calls = 0;
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) throw new HttpRequestException();
            return Task.FromResult(Solution("{\"results\":[]}"));
        })), _ => Task.FromResult(configured), _clock);
        using var client = Client(solver, _ => Challenge());
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        configured = new(url, timeout);
        Assert.Equal("{\"results\":[]}", await client.GetStringAsync(Target));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("{\"results\":[]}", 200, "solver-body", "json", null)]
    [InlineData("invalid json", 200, "cookies", "text", false)]
    [InlineData("<title>Just a moment...</title>", 200, "none", "challenge", true)]
    [InlineData("{\"results\":[]}", 503, "none", "json", true)]
    public async Task EachSolveLogsOnlySafeSummary(string body, int status, string path, string kind, bool? challenged)
    {
        var logs = new List<string>();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(new LogProvider(logs)));
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => Task.FromResult(Solution(body, status: status)))),
            _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 60)), _clock, logging.CreateLogger<CloudflareChallengeSolver>());
        using var client = Client(solver, request => path == "cookies" && request.Headers.Contains("Cookie") ? Reply("words") : Challenge());
        if (path == "none") await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        else await client.GetAsync(Target);
        if (path == "none") await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        else await client.GetAsync(Target);
        var log = Assert.Single(logs);
        Assert.Contains("host=lyrics.example.test", log);
        Assert.Contains("solverHttpStatus=200", log);
        Assert.Contains("solutionStatus=" + status, log);
        Assert.Contains("contentKind=" + kind, log);
        Assert.Contains("clearanceCount=1", log);
        Assert.Contains("retryRechallenged=" + (challenged?.ToString() ?? "not-attempted"), log);
        Assert.Contains("deliveryPath=" + path, log);
        Assert.Contains("elapsedMs=", log);
        Assert.DoesNotContain("private-query", log);
        Assert.DoesNotContain("clearance-secret", log);
        Assert.DoesNotContain("FixtureBrowser", log);
        Assert.DoesNotContain("results", log);
    }

    [Fact]
    public async Task TransportFailureLogsOneSafeSummaryAndBacksOff()
    {
        var logs = new List<string>();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(new LogProvider(logs)));
        var solver = new CloudflareChallengeSolver(new HttpClient(new Handler(_ => Task.FromResult(Reply("private-body", 503)))),
            _ => Task.FromResult(new ChallengeSolverOptions("http://solver:8191", 60)), _clock, logging.CreateLogger<CloudflareChallengeSolver>());
        using var client = Client(solver, _ => Challenge());
        for (var i = 0; i < 2; i++) await Assert.ThrowsAsync<CloudflareChallengeException>(() => client.GetAsync(Target));
        var log = Assert.Single(logs);
        Assert.Contains("solverHttpStatus=503", log);
        Assert.Contains("contentKind=(null)", log);
        Assert.Contains("deliveryFailureReason=not-attempted", log);
        Assert.Contains("deliveryPath=none", log);
        Assert.DoesNotContain("private-body", log);
        Assert.DoesNotContain("private-query", log);
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
        new(new CloudflareChallengeHandler(solver, ["lyrics.example.test"]) { InnerHandler = new Handler(request => Task.FromResult(send(request))) });
    private HttpResponseMessage Solution(string body = "words", string url = Target, string contentType = "application/json", int status = 200) => Reply(JsonSerializer.Serialize(new
    {
        status = "ok",
        solution = new
        {
            url,
            status,
            response = body,
            headers = new Dictionary<string, string> { ["content-type"] = contentType },
            userAgent = "FixtureBrowser/1.0",
            cookies = new[] { new { name = "cf_clearance", value = "clearance-secret", domain = ".example.test", path = "/", expires = _clock.Now.AddMinutes(1).ToUnixTimeSeconds(), secure = true } }
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
