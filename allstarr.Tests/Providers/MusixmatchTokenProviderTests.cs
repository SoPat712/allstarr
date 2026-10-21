using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class MusixmatchTokenProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TokenRejectionBacksOffThenAllowsAnotherAttempt(bool httpFailure)
    {
        var time = new ManualTime();
        var handler = new TokenHandler(httpFailure ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
            """{"message":{"header":{"status_code":401},"body":{}}}""");
        var tokens = new MusixmatchTokenProvider(new HttpClient(handler), time);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await tokens.GetAsync(null, default);
            Assert.Equal(ProviderErrorKind.RateLimited, result.Error?.Kind);
            Assert.True(result.Error?.RetryAfter > TimeSpan.Zero);
        }
        Assert.Equal(1, handler.Requests);
        time.Now += TimeSpan.FromMinutes(6);
        await tokens.GetAsync(null, default);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task SuccessfulTokenIsCachedUntilThatTokenIsRejected()
    {
        var handler = new TokenHandler(HttpStatusCode.OK,
            """{"message":{"header":{"status_code":200},"body":{"user_token":"fixture-token"}}}""");
        var tokens = new MusixmatchTokenProvider(new HttpClient(handler));

        Assert.Equal("fixture-token", (await tokens.GetAsync(null, default)).RequireValue().UserToken);
        await tokens.GetAsync(null, default);
        await tokens.GetAsync("older-token", default);
        Assert.Equal(1, handler.Requests);
        await tokens.GetAsync("fixture-token", default);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"message\":{\"header\":{\"status_code\":200},\"body\":{}}}")]
    public async Task InvalidTokenResponsesAreNotCachedAndArePaced(string payload)
    {
        var handler = new TokenHandler(HttpStatusCode.OK, payload);
        var tokens = new MusixmatchTokenProvider(new HttpClient(handler));

        Assert.False((await tokens.GetAsync(null, default)).IsSuccess);
        Assert.Equal(ProviderErrorKind.RateLimited, (await tokens.GetAsync(null, default)).Error?.Kind);
        Assert.Equal(1, handler.Requests);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TokenHandler(HttpStatusCode status, string payload) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("apic-appmobile.musixmatch.com", request.RequestUri!.Host);
            Assert.Equal("/ws/1.1/token.get", request.RequestUri.AbsolutePath);
            Assert.Equal("?app_id=mac-ios-v2.0", request.RequestUri.Query);
            Assert.Equal("10.1.1", Assert.Single(request.Headers.GetValues("x-mxm-app-version")));
            Assert.Equal("x-mxm-token-guid=", Assert.Single(request.Headers.GetValues("Cookie")));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}
