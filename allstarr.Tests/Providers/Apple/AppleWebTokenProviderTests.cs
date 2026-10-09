using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Providers.AppleMusicKit;

namespace allstarr.Tests;

public sealed class AppleWebTokenProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private const string Homepage = """<script type="module" src="/assets/index-fixture.js"></script>""";

    [Theory]
    [InlineData("/assets/index~fixture.js")]
    [InlineData("/assets/index-fixture.js")]
    [InlineData("/assets/index-legacy-fixture.js")]
    [InlineData("https://music.apple.com/assets/index-fixture.js")]
    public async Task GetAsync_ScrapesAndCachesPublicTokenWithoutAccountHeaders(string script)
    {
        var clock = new ManualTimeProvider(Now);
        var expected = Token(Now.AddHours(2));
        var paths = new List<string>();
        using var http = Http((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("music.apple.com", request.RequestUri.Host);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Authorization"));
            paths.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(Text(paths.Count == 1 ? $"<script src=\"{script}\"></script>" : Script(expected)));
        });
        var provider = new AppleWebTokenProvider(http, clock);
        Assert.Null(provider.FailureCode);
        Assert.Null(provider.ObservedAt);

        Assert.Equal(expected, await provider.GetAsync(CancellationToken.None));
        Assert.Equal(expected, await provider.GetAsync(CancellationToken.None));

        Assert.Equal(["/", new Uri(new Uri("https://music.apple.com/"), script).AbsolutePath], paths);
        Assert.Null(provider.FailureCode);
        Assert.Equal(Now, provider.ObservedAt);
    }

    [Fact]
    public async Task GetAsync_RefreshesAtExpirySafetyMargin()
    {
        var clock = new ManualTimeProvider(Now);
        var first = Token(Now.AddMinutes(2));
        var next = Token(Now.AddHours(2));
        var scripts = 0;
        using var http = Http((request, _) => Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/"
            ? Homepage : Script(++scripts == 1 ? first : next))));
        var provider = new AppleWebTokenProvider(http, clock);

        Assert.Equal(first, await provider.GetAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(first, await provider.GetAsync(CancellationToken.None));
        Assert.Equal(1, scripts);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(next, await provider.GetAsync(CancellationToken.None));

        Assert.Equal(2, scripts);
        Assert.Equal(Now.AddMinutes(1), provider.ObservedAt);
    }

    [Fact]
    public async Task GetAsync_CapsCacheAtSixHours()
    {
        var clock = new ManualTimeProvider(Now);
        var token = Token(Now.AddDays(2));
        var scripts = 0;
        using var http = Http((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/") return Task.FromResult(Text(Homepage));
            scripts++;
            return Task.FromResult(Text(Script(token)));
        });
        var provider = new AppleWebTokenProvider(http, clock);
        await provider.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        await provider.GetAsync(CancellationToken.None);
        Assert.Equal(1, scripts);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(token, await provider.GetAsync(CancellationToken.None));

        Assert.Equal(2, scripts);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"exp":"9999999999"}""")]
    [InlineData("""{"exp":null}""")]
    [InlineData("""{"exp":0}""")]
    [InlineData("""{"exp":9223372036854775807}""")]
    [InlineData("""{"exp":9999999999999999999999999}""")]
    public async Task GetAsync_InvalidJwtPayloadHasSafeFailure(string payload)
    {
        var clock = new ManualTimeProvider(Now);
        using var http = Http((request, _) => Task.FromResult(Text(
            request.RequestUri!.AbsolutePath == "/" ? Homepage : Script(Token(payload)))));
        var provider = new AppleWebTokenProvider(http, clock);

        var exception = await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(() => provider.GetAsync(CancellationToken.None));

        AssertSafeFailure(exception, provider);
        Assert.Equal(Now, provider.ObservedAt);
    }

    [Fact]
    public async Task GetAsync_RejectsMalformedJwtAndExpiredCandidatesBeforeChoosingValidToken()
    {
        var expired = Token(Now);
        var valid = Token(Now.AddHours(2));
        var malformed = "eyJnotjson.eyJnotjson.fixture";
        using var http = Http((request, _) => Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/"
            ? Homepage : Script(malformed) + Script(expired) + Script(valid))));
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        Assert.Equal(valid, await provider.GetAsync(CancellationToken.None));
        Assert.Null(provider.FailureCode);
    }

    [Fact]
    public async Task GetAsync_ExpiredTokenCannotBeReturned()
    {
        using var http = Http((request, _) => Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/"
            ? Homepage : Script(Token(Now)))));
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.GetAsync(CancellationToken.None)), provider);
    }

    [Fact]
    public async Task FailureCache_PreventsRetryBurstsAndRecoversAfterThirtySeconds()
    {
        var clock = new ManualTimeProvider(Now);
        var requests = 0;
        var expected = Token(Now.AddHours(2));
        using var http = Http((request, _) =>
        {
            requests++;
            return Task.FromResult(Text(requests == 1 ? "<html>no script</html>" :
                request.RequestUri!.AbsolutePath == "/" ? Homepage : Script(expected)));
        });
        var provider = new AppleWebTokenProvider(http, clock);

        await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(() => provider.GetAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(29));
        await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(() => provider.GetAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(() => provider.GetLeaseAsync(CancellationToken.None));
        Assert.Equal(1, requests);
        Assert.Equal(Now, provider.ObservedAt);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(expected, await provider.GetAsync(CancellationToken.None));

        Assert.Equal(3, requests);
        Assert.Null(provider.FailureCode);
        Assert.Equal(Now.AddSeconds(30), provider.ObservedAt);
    }

    [Fact]
    public async Task TransportFailure_DoesNotExposeMessageOrInnerException()
    {
        using var http = Http((_, _) => throw new HttpRequestException(
            "https://private.example.test/?token=fixture-secret; private provider body"));
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(() => provider.GetAsync(CancellationToken.None));

        AssertSafeFailure(exception, provider);
        Assert.DoesNotContain("fixture-secret", exception.ToString());
        Assert.DoesNotContain("private.example.test", exception.ToString());
    }

    [Fact]
    public async Task TransportTimeout_RecordsTheSafeAccountStatusFailure()
    {
        using var http = Http((_, _) => throw new TaskCanceledException("private transport timeout"));
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));
        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.GetAsync(CancellationToken.None)), provider);
    }

    [Fact]
    public async Task GetAsync_AllowsThreeManualSameOriginRedirects()
    {
        var paths = new List<string>();
        var expected = Token(Now.AddHours(2));
        using var http = Http((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return Task.FromResult(path switch
            {
                "/" => Redirect("/one"),
                "/one" => Redirect("two"),
                "/two" => Redirect("https://music.apple.com/three"),
                "/three" => Text(Homepage),
                "/assets/index-fixture.js" => Text(Script(expected)),
                _ => throw new InvalidOperationException("Unexpected fixture request")
            });
        });

        Assert.Equal(expected, await new AppleWebTokenProvider(http, new ManualTimeProvider(Now)).GetAsync(CancellationToken.None));
        Assert.Equal(["/", "/one", "/two", "/three", "/assets/index-fixture.js"], paths);
    }

    [Theory]
    [InlineData("http://music.apple.com/unsafe")]
    [InlineData("https://external.example.test/unsafe")]
    [InlineData("https://music.apple.com:444/unsafe")]
    [InlineData("https://fixture:secret@music.apple.com/unsafe")]
    [InlineData("https://music.apple.com/unsafe#fragment")]
    [InlineData("//external.example.test/unsafe")]
    public async Task GetAsync_UnsafeRedirectIsRejectedBeforeDestinationRequest(string destination)
    {
        var requests = 0;
        using var http = Http((_, _) =>
        {
            requests++;
            return Task.FromResult(Redirect(destination));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(() => provider.GetAsync(CancellationToken.None));

        AssertSafeFailure(exception, provider);
        Assert.Equal(1, requests);
        Assert.DoesNotContain(destination, exception.ToString());
    }

    [Fact]
    public async Task GetAsync_ScriptRedirectCannotLeaveAppleOrigin()
    {
        var requests = 0;
        using var http = Http((_, _) =>
        {
            requests++;
            return Task.FromResult(requests == 1 ? Text(Homepage) : Redirect("https://external.example.test/index.js"));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.GetAsync(CancellationToken.None)), provider);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task GetAsync_RedirectLoopIsBounded()
    {
        var requests = 0;
        using var http = Http((_, _) =>
        {
            requests++;
            return Task.FromResult(Redirect("/"));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.GetAsync(CancellationToken.None)), provider);
        Assert.Equal(4, requests);
    }

    [Theory]
    [InlineData("https://external.example.test/assets/index-fixture.js")]
    [InlineData("http://music.apple.com/assets/index-fixture.js")]
    [InlineData("//external.example.test/assets/index-fixture.js")]
    public async Task GetAsync_UnsafeScriptReferenceIsRejected(string script)
    {
        var requests = 0;
        using var http = Http((_, _) =>
        {
            requests++;
            return Task.FromResult(Text($"<script src=\"{script}\"></script>"));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.GetAsync(CancellationToken.None)), provider);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task GetAsync_EnforcesHomepageAndScriptByteLimits(bool script, bool declaredLength)
    {
        var requests = 0;
        var oversized = new SizedContent((script ? 16 : 8) * 1024 * 1024 + 1, declaredLength);
        using var http = Http((_, _) =>
        {
            requests++;
            return Task.FromResult(script && requests == 1 ? Text(Homepage) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = oversized
            });
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.GetAsync(CancellationToken.None)), provider);

        Assert.Equal(script ? 2 : 1, requests);
        Assert.True(oversized.Disposed);
        Assert.Equal(!declaredLength, oversized.ReadAttempted);
    }

    [Fact]
    public async Task GetLeaseAsync_ConcurrentInitializationFetchesOnlyOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var expected = Token(Now.AddHours(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = Http(async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/") return Text(Homepage);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Text(Script(expected));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        var first = provider.GetLeaseAsync(timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var second = provider.GetLeaseAsync(timeout.Token);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(expected, result.Token));
        Assert.Same(results[0], results[1]);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_LeaseCoalescesStaggeredRejectionsEvenWhenTokenIsUnchanged(bool unchanged)
    {
        var clock = new ManualTimeProvider(Now);
        var original = Token(Now.AddHours(2));
        var refreshed = unchanged ? original : Token(Now.AddHours(3));
        var requests = 0;
        using var http = Http((request, _) =>
        {
            requests++;
            return Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/"
                ? Homepage : Script(requests == 2 ? original : refreshed)));
        });
        var provider = new AppleWebTokenProvider(http, clock);
        // Both requests acquire their lease before either receives a rejection.
        var firstRequest = await provider.GetLeaseAsync(CancellationToken.None);
        var delayedRequest = await provider.GetLeaseAsync(CancellationToken.None);
        Assert.Same(firstRequest, delayedRequest);
        Assert.Equal(original, firstRequest.Token);
        Assert.DoesNotContain(original, firstRequest.ToString());

        var firstRefresh = await provider.RefreshAsync(firstRequest, CancellationToken.None);
        Assert.Equal(refreshed, firstRefresh.Token);
        Assert.True(firstRefresh.Generation > firstRequest.Generation);
        Assert.Equal(4, requests);
        clock.Advance(TimeSpan.FromSeconds(10));

        var delayedRefresh = await provider.RefreshAsync(delayedRequest, CancellationToken.None);
        Assert.Same(firstRefresh, delayedRefresh);
        Assert.Same(firstRefresh, await provider.GetLeaseAsync(CancellationToken.None));
        Assert.Equal(4, requests);

        // A rejection of the newly issued lease may start its own refresh wave.
        var nextRefresh = await provider.RefreshAsync(firstRefresh, CancellationToken.None);
        Assert.True(nextRefresh.Generation > firstRefresh.Generation);
        Assert.Same(nextRefresh, await provider.RefreshAsync(firstRefresh, CancellationToken.None));
        Assert.Same(nextRefresh, await provider.RefreshAsync(delayedRequest, CancellationToken.None));
        Assert.Equal(6, requests);
        Assert.Null(provider.FailureCode);
    }

    [Fact]
    public async Task RefreshAsync_LeaseSharesFailedRefreshAndUsesRecoveredObservation()
    {
        var clock = new ManualTimeProvider(Now);
        var token = Token(Now.AddHours(2));
        var requests = 0;
        using var http = Http((request, _) =>
        {
            requests++;
            return Task.FromResult(Text(requests == 3 ? "<html>no script</html>" :
                request.RequestUri!.AbsolutePath == "/" ? Homepage : Script(token)));
        });
        var provider = new AppleWebTokenProvider(http, clock);
        var rejected = await provider.GetLeaseAsync(CancellationToken.None);

        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.RefreshAsync(rejected, CancellationToken.None)), provider);
        clock.Advance(TimeSpan.FromSeconds(29));
        AssertSafeFailure(await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.RefreshAsync(rejected, CancellationToken.None)), provider);
        Assert.Equal(3, requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.RefreshAsync(rejected, CancellationToken.None));
        Assert.Equal(3, requests);

        var recovered = await provider.GetLeaseAsync(CancellationToken.None);
        Assert.Equal(token, recovered.Token);
        Assert.True(recovered.Generation > rejected.Generation);
        Assert.Same(recovered, await provider.RefreshAsync(rejected, CancellationToken.None));
        Assert.Equal(5, requests);
        Assert.Null(provider.FailureCode);
    }

    [Fact]
    public async Task RefreshAsync_ConsumedLeaseCannotStartAnotherRefreshAfterCacheExpiry()
    {
        var clock = new ManualTimeProvider(Now);
        var token = Token(Now.AddDays(2));
        var requests = 0;
        using var http = Http((request, _) =>
        {
            requests++;
            return Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/" ? Homepage : Script(token)));
        });
        var provider = new AppleWebTokenProvider(http, clock);
        var rejected = await provider.GetLeaseAsync(CancellationToken.None);
        await provider.RefreshAsync(rejected, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(6));

        await Assert.ThrowsAsync<AppleWebTokenUnavailableException>(
            () => provider.RefreshAsync(rejected, CancellationToken.None));
        Assert.Equal(4, requests);

        var current = await provider.GetLeaseAsync(CancellationToken.None);
        Assert.Equal(token, current.Token);
        Assert.Same(current, await provider.RefreshAsync(rejected, CancellationToken.None));
        Assert.Equal(6, requests);
        Assert.Null(provider.FailureCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_CoalescesConcurrentRejectionsEvenWhenTokenIsUnchanged(bool unchanged)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = Token(Now.AddHours(2));
        var refreshed = unchanged ? original : Token(Now.AddHours(3));
        var requests = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = Http(async (request, cancellationToken) =>
        {
            var count = Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/") return Text(Homepage);
            if (count == 2) return Text(Script(original));
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Text(Script(refreshed));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));
        var lease = await provider.GetLeaseAsync(timeout.Token);
        Assert.Equal(original, lease.Token);

        var first = provider.RefreshAsync(lease, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var second = provider.RefreshAsync(lease, timeout.Token);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(refreshed, result.Token));
        Assert.Same(results[0], results[1]);
        Assert.Equal(refreshed, await provider.GetAsync(timeout.Token));
        Assert.Equal(4, requests);
        Assert.Null(provider.FailureCode);
    }

    [Fact]
    public async Task RefreshAsync_RejectionForOlderLeaseUsesAlreadyUpdatedCache()
    {
        var requests = 0;
        var token = Token(Now.AddHours(2));
        var refreshed = Token(Now.AddHours(3));
        using var http = Http((request, _) =>
        {
            requests++;
            return Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/"
                ? Homepage : Script(requests == 2 ? token : refreshed)));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));
        var original = await provider.GetLeaseAsync(CancellationToken.None);
        var current = await provider.RefreshAsync(original, CancellationToken.None);
        Assert.Equal(refreshed, current.Token);

        Assert.Same(current, await provider.RefreshAsync(original, CancellationToken.None));
        Assert.Equal(4, requests);
    }

    [Fact]
    public async Task GetAsync_CancellationDoesNotCacheFailure()
    {
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        var token = Token(Now.AddHours(2));
        using var http = Http((request, cancellationToken) =>
        {
            requests++;
            if (requests == 1)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/" ? Homepage : Script(token)));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(cancellation.Token));
        Assert.Null(provider.FailureCode);
        Assert.Null(provider.ObservedAt);
        Assert.Equal(token, await provider.GetAsync(CancellationToken.None));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task GetAsync_AlreadyCanceledDoesNotReturnCacheOrIssueRequests()
    {
        var requests = 0;
        var token = Token(Now.AddHours(2));
        using var cancellation = new CancellationTokenSource();
        using var http = Http((_, _) =>
        {
            requests++;
            return Task.FromResult(Text(requests == 1 ? Homepage : Script(token)));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));
        var lease = await provider.GetLeaseAsync(CancellationToken.None);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetLeaseAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RefreshAsync(lease, cancellation.Token));

        Assert.Equal(2, requests);
        Assert.Null(provider.FailureCode);
        Assert.Equal(Now, provider.ObservedAt);
    }

    [Fact]
    public async Task RefreshAsync_CanceledWaiterDoesNotCancelRefresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var token = Token(Now.AddHours(2));
        using var cancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = Http(async (request, cancellationToken) =>
        {
            var count = Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/") return Text(Homepage);
            if (count > 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return Text(Script(token));
        });
        var provider = new AppleWebTokenProvider(http, new ManualTimeProvider(Now));
        var lease = await provider.GetLeaseAsync(timeout.Token);

        var refresh = provider.RefreshAsync(lease, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var waiter = provider.RefreshAsync(lease, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult();

        Assert.Equal(token, (await refresh).Token);
        Assert.Equal(4, requests);
        Assert.Null(provider.FailureCode);
    }

    private static void AssertSafeFailure(AppleWebTokenUnavailableException exception, AppleWebTokenProvider provider)
    {
        Assert.Equal("Apple web token is unavailable.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Equal("apple-web-token-unavailable", provider.FailureCode);
    }

    private static string Token(DateTimeOffset expiresAt) =>
        Token(JsonSerializer.Serialize(new { exp = expiresAt.ToUnixTimeSeconds() }));

    private static string Token(string payload) =>
        Base64Url("""{"alg":"ES256","typ":"JWT"}""") + "." + Base64Url(payload) + "." + Base64Url("fixture-signature");

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Script(string token) => $"const fixtureBearer = \"{token}\";";

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/plain")
    };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new FixtureHandler(send));

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class SizedContent(int length, bool declaredLength) : HttpContent
    {
        public bool Disposed { get; private set; }
        public bool ReadAttempted { get; private set; }

        protected override bool TryComputeLength(out long value)
        {
            value = length;
            return declaredLength;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            var chunk = new byte[8192];
            for (var remaining = length; remaining > 0; remaining -= chunk.Length)
                await stream.WriteAsync(chunk.AsMemory(0, Math.Min(remaining, chunk.Length)));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
