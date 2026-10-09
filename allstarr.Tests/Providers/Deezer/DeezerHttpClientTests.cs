using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Providers.Deezer;

namespace allstarr.Tests;

public sealed class DeezerHttpClientTests
{
    [Theory]
    [InlineData(429, "{}")]
    [InlineData(503, "{}")]
    [InlineData(502, "{}")]
    [InlineData(200, "{\"error\":{\"code\":4}}")]
    public async Task TransientResponses_RecoverWithFreshDisposedRequests(int status, string body)
    {
        var clock = new TestClock();
        var requests = new List<TrackedContent>();
        var responses = new List<TrackedContent>();
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (calls > 0) Assert.True(requests[calls - 1].Disposed);
            var content = new TrackedContent(calls++ == 0 ? body : "{\"id\":42}");
            responses.Add(content);
            return Task.FromResult(new HttpResponseMessage(calls == 1 ? (HttpStatusCode)status : HttpStatusCode.OK)
            { Content = content });
        }));
        var client = new DeezerHttpClient(http, 0, clock.DelayAsync, () => clock.Now);

        using var response = await client.SendAsync(() =>
        {
            var content = new TrackedContent("fixture request");
            requests.Add(content);
            return new HttpRequestMessage(HttpMethod.Post, "https://api.deezer.com/fixture") { Content = content };
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"id\":42}", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(500)], clock.Delays);
        Assert.All(requests, content => Assert.True(content.Disposed));
        Assert.True(responses[0].Disposed);
        Assert.False(responses[1].Disposed);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task HttpThrottleExhaustion_ReturnsFinalResponseToCaller(int status)
    {
        var clock = new TestClock();
        var contents = new List<TrackedContent>();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var content = new TrackedContent("{}");
            contents.Add(content);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        }));
        var client = new DeezerHttpClient(http, 0, clock.DelayAsync, () => clock.Now);

        using var response = await client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(4, contents.Count);
        Assert.All(contents.Take(3), content => Assert.True(content.Disposed));
        Assert.False(contents[3].Disposed);
        Assert.Equal(new[] { 500d, 1000d, 2000d }, clock.Delays.Select(delay => delay.TotalMilliseconds));
    }

    [Fact]
    public async Task JsonQuotaExhaustion_ThrowsSafe429AndDisposesEveryResponse()
    {
        var clock = new TestClock();
        var contents = new List<TrackedContent>();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var content = new TrackedContent("{\"error\":{\"code\":4,\"message\":\"private fixture value\"}}");
            contents.Add(content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var client = new DeezerHttpClient(http, 0, clock.DelayAsync, () => clock.Now);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.DoesNotContain("private fixture value", exception.Message);
        Assert.Equal(4, contents.Count);
        Assert.All(contents, content => Assert.True(content.Disposed));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task PermanentHttpFailure_IsReturnedWithoutRetry(int status)
    {
        var calls = 0;
        var clock = new TestClock();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        }));
        var client = new DeezerHttpClient(http, 0, clock.DelayAsync, () => clock.Now);

        using var response = await client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Empty(clock.Delays);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"4\"}}")]
    [InlineData("{\"error\":{\"code\":800}}")]
    [InlineData("[]")]
    public async Task NonQuotaJson_DoesNotRetry(string body)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        var client = new DeezerHttpClient(http, 0);

        using var response = await client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None);

        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfter_IsRespectedForDeltaAndDate(bool useDate)
    {
        var calls = 0;
        var clock = new TestClock();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(calls++ == 0 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            { Content = new StringContent("{}") };
            response.Headers.RetryAfter = useDate
                ? new RetryConditionHeaderValue(clock.Now.AddSeconds(2))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        }));
        var client = new DeezerHttpClient(http, 0, clock.DelayAsync, () => clock.Now);

        using var response = await client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(2)], clock.Delays);
    }

    [Fact]
    public async Task RetryAfterBeyondBudget_DoesNotRetryEarly()
    {
        var calls = 0;
        var clock = new TestClock();
        var content = new TrackedContent("{}");
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        }));
        var client = new DeezerHttpClient(http, 0, clock.DelayAsync, () => clock.Now);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(1, calls);
        Assert.Empty(clock.Delays);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task AllRequests_ShareMinimumSpacing()
    {
        var clock = new TestClock();
        var starts = new List<DateTimeOffset>();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            starts.Add(clock.Now);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }));
        var client = new DeezerHttpClient(http, 200, clock.DelayAsync, () => clock.Now);

        using var first = await client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None);
        using var second = await client.GetAsync("https://api.deezer.com/track/43", CancellationToken.None);
        using var third = await client.GetAsync("https://api.deezer.com/track/44", CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(200), starts[1] - starts[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), starts[2] - starts[1]);
    }

    [Fact]
    public async Task CancellationDuringBackoff_StopsBeforeAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        var client = new DeezerHttpClient(http, 0, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("https://api.deezer.com/track/42", cancellation.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationDuringSpacing_StopsBeforeSendingNextRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }));
        var client = new DeezerHttpClient(http, 200, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        }, () => DateTimeOffset.UnixEpoch);
        using var first = await client.GetAsync("https://api.deezer.com/track/42", CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("https://api.deezer.com/track/43", cancellation.Token));

        Assert.Equal(1, calls);
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            Now += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class TrackedContent(string body) : StringContent(body)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
