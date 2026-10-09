using System.Net;
using System.Text;
using allstarr.Core.Providers.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class QobuzBundleServiceTests : IDisposable
{
    private const string LoginPage = """<html><script src="/resources/1.0.3-b001/bundle.js"></script></html>""";
    private readonly List<HttpClient> _clients = [];

    [Fact]
    public async Task GetSigningCredentialsAsync_CachesCompleteBundleAndPreservesCandidateOrder()
    {
        var requests = 0;
        var service = CreateService((request, _) =>
        {
            requests++;
            Assert.False(request.Headers.Contains("X-User-Auth-Token"));
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/login"
                ? LoginPage : Bundle("123456789", "first-secret", "second-secret", "third-secret")));
        });

        var first = await service.GetSigningCredentialsAsync(CancellationToken.None);
        var second = await service.GetSigningCredentialsAsync(CancellationToken.None);
        var mutableCopy = await service.GetSecretsAsync();
        mutableCopy.Clear();

        Assert.Same(first, second);
        Assert.Equal("123456789", await service.GetAppIdAsync());
        Assert.Equal(["second-secret", "first-secret", "third-secret"], first.Secrets);
        Assert.Equal(first.Secrets, await service.GetSecretsAsync(CancellationToken.None));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task GetSigningCredentialsAsync_ConcurrentInitializationFetchesOneCompleteBundle()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = CreateService(async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/login") return Text(LoginPage);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Text(Bundle("123456789", "first-secret"));
        });

        var first = service.GetSigningCredentialsAsync(timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var second = service.GetSigningCredentialsAsync(timeout.Token);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0], results[1]);
        Assert.Equal("123456789", results[0].AppId);
        Assert.Equal(["first-secret"], results[0].Secrets);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("987654321")]
    public async Task RefreshAsync_ConcurrentFailuresCoalesceAndPublishWholeSnapshot(string refreshedAppId)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bundleRequests = 0;
        var loginRequests = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = CreateService(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login")
            {
                Interlocked.Increment(ref loginRequests);
                return Text(LoginPage);
            }
            if (Interlocked.Increment(ref bundleRequests) == 1)
                return Text(Bundle("123456789", "original-secret"));
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Text(Bundle(refreshedAppId, "refreshed-secret"));
        });
        var original = await service.GetSigningCredentialsAsync(timeout.Token);

        var first = service.RefreshAsync(original.AppId, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var second = service.RefreshAsync(original.AppId, timeout.Token);
        Assert.False(second.IsCompleted);
        // Readers can continue using the old complete snapshot during refresh.
        Assert.Same(original, await service.GetSigningCredentialsAsync(timeout.Token));
        release.TrySetResult();
        await Task.WhenAll(first, second);
        var refreshed = await service.GetSigningCredentialsAsync(timeout.Token);

        Assert.NotSame(original, refreshed);
        Assert.Equal(refreshedAppId, refreshed.AppId);
        Assert.Equal(["refreshed-secret"], refreshed.Secrets);
        Assert.Equal(["original-secret"], original.Secrets);
        Assert.Equal(2, loginRequests);
        Assert.Equal(2, bundleRequests);
    }

    [Fact]
    public async Task RefreshAsync_OldAppIdAfterRefreshDoesNotFetchAgain()
    {
        var bundleRequests = 0;
        var service = CreateService((request, _) => Task.FromResult(Text(
            request.RequestUri!.AbsolutePath == "/login" ? LoginPage :
            ++bundleRequests == 1 ? Bundle("123456789", "original-secret") : Bundle("987654321", "refreshed-secret"))));
        await service.GetSigningCredentialsAsync(CancellationToken.None);
        await service.RefreshAsync("123456789", CancellationToken.None);

        await service.RefreshAsync("123456789", CancellationToken.None);

        Assert.Equal(2, bundleRequests);
        Assert.Equal("987654321", (await service.GetSigningCredentialsAsync(CancellationToken.None)).AppId);
    }

    [Theory]
    [InlineData("missing-app")]
    [InlineData("missing-secrets")]
    [InlineData("partial-secret")]
    [InlineData("empty-secret")]
    [InlineData("invalid-encoding")]
    public async Task GetSigningCredentialsAsync_InvalidInitialBundleDoesNotPoisonRecovery(string failure)
    {
        var bundleRequests = 0;
        var invalid = failure switch
        {
            "missing-app" => "a.initialSeed(\"ZmFrZQ==\",window.utimezone.paris)",
            "missing-secrets" => AppId("123456789"),
            "partial-secret" => AppId("123456789") + "a.initialSeed(\"ZmFrZQ==\",window.utimezone.paris)",
            "empty-secret" => Bundle("123456789", ""),
            "invalid-encoding" => AppId("123456789") + Candidate("paris", "________"),
            _ => throw new InvalidOperationException()
        };
        var service = CreateService((request, _) => Task.FromResult(Text(
            request.RequestUri!.AbsolutePath == "/login" ? LoginPage :
            ++bundleRequests == 1 ? invalid : Bundle("987654321", "recovered-secret"))));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GetSigningCredentialsAsync(CancellationToken.None));
        var recovered = await service.GetSigningCredentialsAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("987654321", recovered.AppId);
        Assert.Equal(["recovered-secret"], recovered.Secrets);
        Assert.Equal(2, bundleRequests);
    }

    [Fact]
    public async Task GetSigningCredentialsAsync_InvalidLoginPageDoesNotCachePartialInitialization()
    {
        var loginRequests = 0;
        var bundleRequests = 0;
        var service = CreateService((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login")
                return Task.FromResult(Text(++loginRequests == 1 ? "<html>unavailable</html>" : LoginPage));
            bundleRequests++;
            return Task.FromResult(Text(Bundle("123456789", "recovered-secret")));
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GetSigningCredentialsAsync(CancellationToken.None));
        var recovered = await service.GetSigningCredentialsAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(["recovered-secret"], recovered.Secrets);
        Assert.Equal(2, loginRequests);
        Assert.Equal(1, bundleRequests);
    }

    [Fact]
    public async Task RefreshAsync_InvalidReplacementPreservesLastCompleteBundleAndCanRecover()
    {
        var bundleRequests = 0;
        var service = CreateService((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login") return Task.FromResult(Text(LoginPage));
            return Task.FromResult(Text(++bundleRequests switch
            {
                1 => Bundle("123456789", "original-secret"),
                2 => AppId("987654321"),
                _ => Bundle("987654321", "refreshed-secret")
            }));
        });
        var original = await service.GetSigningCredentialsAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.RefreshAsync(original.AppId, CancellationToken.None));
        Assert.Same(original, await service.GetSigningCredentialsAsync(CancellationToken.None));
        await service.RefreshAsync(original.AppId, CancellationToken.None);
        var refreshed = await service.GetSigningCredentialsAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("987654321", refreshed.AppId);
        Assert.Equal(["refreshed-secret"], refreshed.Secrets);
        Assert.Equal(3, bundleRequests);
    }

    [Fact]
    public async Task GetSigningCredentialsAsync_CanceledFetchReleasesInitializationForNextCall()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bundleRequests = 0;
        using var cancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = CreateService(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login") return Text(LoginPage);
            if (Interlocked.Increment(ref bundleRequests) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Text(Bundle("123456789", "recovered-secret"));
        });

        var first = service.GetSigningCredentialsAsync(cancellation.Token);
        await entered.Task.WaitAsync(timeout.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var recovered = await service.GetSigningCredentialsAsync(timeout.Token);

        Assert.Equal(["recovered-secret"], recovered.Secrets);
        Assert.Equal(2, bundleRequests);
    }

    [Fact]
    public async Task RefreshAsync_CanceledWaiterDoesNotCancelSharedRefresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bundleRequests = 0;
        using var cancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = CreateService(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login") return Text(LoginPage);
            if (Interlocked.Increment(ref bundleRequests) == 1) return Text(Bundle("123456789", "original-secret"));
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Text(Bundle("987654321", "refreshed-secret"));
        });
        await service.GetSigningCredentialsAsync(timeout.Token);

        var refresh = service.RefreshAsync("123456789", timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var waiter = service.RefreshAsync("123456789", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult();
        await refresh;
        var refreshed = await service.GetSigningCredentialsAsync(timeout.Token);

        Assert.Equal("987654321", refreshed.AppId);
        Assert.Equal(["refreshed-secret"], refreshed.Secrets);
        Assert.Equal(2, bundleRequests);
    }

    [Fact]
    public async Task RefreshAsync_CanceledFetchKeepsPreviousSnapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bundleRequests = 0;
        using var cancellation = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = CreateService(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/login") return Text(LoginPage);
            if (Interlocked.Increment(ref bundleRequests) == 1) return Text(Bundle("123456789", "original-secret"));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Text(Bundle("987654321", "unused-secret"));
        });
        var original = await service.GetSigningCredentialsAsync(timeout.Token);

        var refresh = service.RefreshAsync(original.AppId, cancellation.Token);
        await entered.Task.WaitAsync(timeout.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        Assert.Same(original, await service.GetSigningCredentialsAsync(timeout.Token));
        Assert.Equal(2, bundleRequests);
    }

    [Fact]
    public async Task CachedBundle_AlreadyCanceledCallsDoNotReturnCachedDataOrFetchAgain()
    {
        var requests = 0;
        var service = CreateService((request, _) =>
        {
            requests++;
            return Task.FromResult(Text(request.RequestUri!.AbsolutePath == "/login"
                ? LoginPage : Bundle("123456789", "fixture-secret")));
        });
        await service.GetSigningCredentialsAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetSigningCredentialsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync("123456789", cancellation.Token));

        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetSigningCredentialsAsync_HttpFailureKeepsStatusAndOmitsBody(HttpStatusCode status)
    {
        var requests = 0;
        var service = CreateService((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("private provider detail")
            });
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GetSigningCredentialsAsync(CancellationToken.None));

        Assert.Equal(status, exception.StatusCode);
        Assert.DoesNotContain("private provider detail", exception.ToString());
        Assert.Equal(1, requests);
    }

    private QobuzBundleService CreateService(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var http = new HttpClient(new FixtureHandler(send));
        _clients.Add(http);
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(value => value.CreateClient("QobuzApi")).Returns(http);
        var service = new QobuzBundleService(factory.Object, NullLogger<QobuzBundleService>.Instance);
        factory.Verify(value => value.CreateClient("QobuzApi"), Times.Once);
        return service;
    }

    private static string Bundle(string appId, params string[] secrets)
    {
        string[] timezones = ["paris", "berlin", "rome"];
        return AppId(appId) + string.Join("", secrets.Select((secret, index) =>
            Candidate(timezones[index], Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)))));
    }

    private static string AppId(string appId) =>
        "production:{api:{appId:\"" + appId + "\",appSecret:\"" + new string('a', 32) + "\"";

    private static string Candidate(string timezone, string encodedSecret)
    {
        var payload = encodedSecret + new string('A', 44);
        return "a.initialSeed(\"" + payload[..8] + "\",window.utimezone." + timezone + ")" +
            "name:\"Europe/" + char.ToUpperInvariant(timezone[0]) + timezone[1..] +
            "\",info:\"" + payload[8..16] + "\",extras:\"" + payload[16..] + "\"";
    }

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "text/plain")
    };

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    public void Dispose()
    {
        foreach (var http in _clients) http.Dispose();
    }
}
