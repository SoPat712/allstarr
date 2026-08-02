using Microsoft.Extensions.Logging;

namespace allstarr.Services.Common;

/// <summary>
/// Utility class for handling retry logic with exponential backoff.
/// Centralizes retry patterns used across download and metadata services.
/// </summary>
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

    /// <summary>
    /// Executes an async action with exponential backoff retry logic.
    /// Retries on HTTP 503 (Service Unavailable) and 429 (Too Many Requests).
    /// </summary>
    /// <typeparam name="T">Return type of the action</typeparam>
    /// <param name="action">The async action to execute</param>
    /// <param name="logger">Logger for retry attempts</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3)</param>
    /// <param name="initialDelayMs">Initial delay in milliseconds (default: 1000)</param>
    /// <returns>Result of the action</returns>
    public static async Task<T> RetryWithBackoffAsync<T>(
        Func<Task<T>> action,
        ILogger logger,
        int maxRetries = 3,
        int initialDelayMs = 1000,
        CancellationToken cancellationToken = default)
    {
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
            catch
            {
                throw;
            }
        }

        throw lastException!;
    }

    /// <summary>
    /// Executes an async action with exponential backoff retry logic (void return).
    /// </summary>
    public static async Task RetryWithBackoffAsync(
        Func<Task> action,
        ILogger logger,
        int maxRetries = 3,
        int initialDelayMs = 1000,
        CancellationToken cancellationToken = default)
    {
        await RetryWithBackoffAsync(async () =>
        {
            await action();
            return true;
        }, logger, maxRetries, initialDelayMs, cancellationToken);
    }
}
