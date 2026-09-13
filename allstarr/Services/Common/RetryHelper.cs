using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

public static class RetryHelper
{
    internal static HttpResponseMessage EnsureSuccessOrDispose(HttpResponseMessage response)
    {
        try
        {
            return response.EnsureSuccessStatusCode();
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static async Task<T> RetryWithBackoffAsync<T>(
        Func<Task<T>> action,
        ILogger logger,
        int maxRetries = 3,
        int initialDelayMs = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetries, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(initialDelayMs);
        Exception? lastException = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lastException = ex;
                if (attempt < maxRetries - 1)
                {
                    var delay = initialDelayMs * (int)Math.Pow(2, attempt);
                    logger.LogWarning(
                        "Retry attempt {Attempt}/{MaxRetries} after {Delay}ms ({Message})",
                        attempt + 1, maxRetries, delay, ex.Message);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        throw lastException!;
    }

    public static Task RetryWithBackoffAsync(
        Func<Task> action,
        ILogger logger,
        int maxRetries = 3,
        int initialDelayMs = 1000,
        CancellationToken cancellationToken = default)
    {
        return RetryWithBackoffAsync(async () =>
        {
            await action();
            return true;
        }, logger, maxRetries, initialDelayMs, cancellationToken);
    }
}
