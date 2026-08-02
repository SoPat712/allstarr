using System.Net;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace allstarr.Tests;

public class RetryHelperTests
{
    [Fact]
    public async Task EnsureSuccessOrDispose_FailureDisposesResponse()
    {
        var content = new StringContent("failed");
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content };

        Assert.Throws<HttpRequestException>(() => RetryHelper.EnsureSuccessOrDispose(response));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RetryWithBackoffAsync_ShouldRetryOn503AndSucceed()
    {
        var attempts = 0;

        var result = await RetryHelper.RetryWithBackoffAsync(async () =>
        {
            attempts++;
            await Task.Yield();

            if (attempts < 3)
            {
                throw new HttpRequestException("temporary", null, HttpStatusCode.ServiceUnavailable);
            }

            return "ok";
        }, NullLogger.Instance, maxRetries: 4, initialDelayMs: 1);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryWithBackoffAsync_ShouldRetryOn429ThenThrowAfterMaxRetries()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await RetryHelper.RetryWithBackoffAsync(async () =>
            {
                attempts++;
                await Task.Yield();
                throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
            }, NullLogger.Instance, maxRetries: 3, initialDelayMs: 1));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryWithBackoffAsync_ShouldNotRetryOnNonHttpRequestException()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RetryHelper.RetryWithBackoffAsync(async () =>
            {
                attempts++;
                await Task.Yield();
                throw new InvalidOperationException("fatal");
            }, NullLogger.Instance, maxRetries: 3, initialDelayMs: 1));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryWithBackoffAsync_CancellationStopsBackoff()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RetryHelper.RetryWithBackoffAsync<string>(() =>
            {
                attempts++;
                cancellation.Cancel();
                throw new HttpRequestException("temporary", null, HttpStatusCode.ServiceUnavailable);
            }, NullLogger.Instance, initialDelayMs: 10_000, cancellationToken: cancellation.Token));

        Assert.Equal(1, attempts);
    }
}
