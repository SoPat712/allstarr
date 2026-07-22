namespace allstarr.Services.Common;

/// <summary>
/// Helper for round-robin load balancing with fallback across multiple API endpoints.
/// Distributes load evenly while maintaining reliability through automatic failover.
/// </summary>
public class RoundRobinFallbackHelper
{
    private const int PreferredFastEndpointCount = 2;
    private const int MaximumConcurrentEndpointOperations = 8;
    private readonly List<string> _apiUrls;
    private int _currentUrlIndex = 0;
    private readonly object _urlIndexLock = new object();
    private readonly ILogger _logger;
    private readonly string _serviceName;
    private readonly HttpClient _healthCheckClient;

    // Cache health check results for 30 seconds to avoid excessive checks
    private readonly Dictionary<string, (bool isHealthy, DateTime checkedAt)> _healthCache = new();
    private readonly object _healthCacheLock = new object();
    private readonly TimeSpan _healthCacheExpiry = TimeSpan.FromSeconds(30);

    public int EndpointCount => _apiUrls.Count;

    public RoundRobinFallbackHelper(List<string> apiUrls, ILogger logger, string serviceName)
    {
        _apiUrls = apiUrls ?? throw new ArgumentNullException(nameof(apiUrls));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceName = serviceName ?? "Service";

        // Create a dedicated HttpClient for health checks with short timeout
        _healthCheckClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3) // Quick health check timeout
        };

        if (_apiUrls.Count == 0)
        {
            _logger.LogWarning("{Service} initialized with zero endpoints; external provider is currently unavailable", _serviceName);
        }
    }

    /// <summary>
    /// Quickly checks if an endpoint is healthy (responds within 3 seconds).
    /// Results are cached for 30 seconds to avoid excessive health checks.
    /// </summary>
    private async Task<bool> IsEndpointHealthyAsync(string baseUrl)
    {
        // Check cache first
        lock (_healthCacheLock)
        {
            if (_healthCache.TryGetValue(baseUrl, out var cached))
            {
                if (DateTime.UtcNow - cached.checkedAt < _healthCacheExpiry)
                {
                    return cached.isHealthy;
                }
            }
        }

        // Perform health check
        try
        {
            var response = await _healthCheckClient.GetAsync(baseUrl, HttpCompletionOption.ResponseHeadersRead);
            var isHealthy = response.IsSuccessStatusCode;

            // Cache result
            lock (_healthCacheLock)
            {
                _healthCache[baseUrl] = (isHealthy, DateTime.UtcNow);
            }

            if (!isHealthy)
            {
                _logger.LogDebug("{Service} endpoint {Endpoint} health check failed: {StatusCode}",
                    _serviceName, baseUrl, response.StatusCode);
            }

            return isHealthy;
        }
        catch (TaskCanceledException)
        {
            // Timeouts are expected when checking multiple mirrors - log at debug level
            _logger.LogDebug("{Service} endpoint {Endpoint} health check timed out", _serviceName, baseUrl);

            // Cache as unhealthy
            lock (_healthCacheLock)
            {
                _healthCache[baseUrl] = (false, DateTime.UtcNow);
            }

            return false;
        }
        catch (HttpRequestException ex)
        {
            // Connection errors (refused, DNS failures, etc.) - log at debug level
            _logger.LogDebug("{Service} endpoint {Endpoint} health check failed: {Message}", _serviceName, baseUrl, ex.Message);

            // Cache as unhealthy
            lock (_healthCacheLock)
            {
                _healthCache[baseUrl] = (false, DateTime.UtcNow);
            }

            return false;
        }
        catch (Exception ex)
        {
            // Unexpected errors - still log at debug level for health checks
            _logger.LogDebug(ex, "{Service} endpoint {Endpoint} health check failed", _serviceName, baseUrl);

            // Cache as unhealthy
            lock (_healthCacheLock)
            {
                _healthCache[baseUrl] = (false, DateTime.UtcNow);
            }

            return false;
        }
    }

    /// <summary>
    /// Gets a list of healthy endpoints, checking them in parallel.
    /// Falls back to all endpoints if none are healthy.
    /// </summary>
    private async Task<List<string>> GetHealthyEndpointsAsync()
    {
        if (_apiUrls.Count == 0)
        {
            return new List<string>();
        }

        using var healthGate = new SemaphoreSlim(MaximumConcurrentEndpointOperations);
        var healthCheckTasks = _apiUrls.Select(async url =>
        {
            await healthGate.WaitAsync();
            try
            {
                return new { Url = url, IsHealthy = await IsEndpointHealthyAsync(url) };
            }
            finally
            {
                healthGate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(healthCheckTasks);
        var healthyEndpoints = results.Where(r => r.IsHealthy).Select(r => r.Url).ToList();

        if (healthyEndpoints.Count == 0)
        {
            _logger.LogWarning("{Service} health check: no healthy endpoints found, will try all", _serviceName);
            return _apiUrls;
        }

        _logger.LogDebug("{Service} health check: {Healthy}/{Total} endpoints healthy",
            _serviceName, healthyEndpoints.Count, _apiUrls.Count);

        return healthyEndpoints;
    }

    private List<string> BuildTryOrder(List<string> endpointsToTry)
    {
        if (endpointsToTry.Count <= 1)
        {
            return endpointsToTry;
        }

        // Prefer the fastest endpoints first (benchmark order), while still keeping
        // all remaining endpoints available as fallback.
        var preferredCount = Math.Min(PreferredFastEndpointCount, endpointsToTry.Count);

        int preferredStartIndex;
        lock (_urlIndexLock)
        {
            preferredStartIndex = _currentUrlIndex % preferredCount;
            _currentUrlIndex = (_currentUrlIndex + 1) % preferredCount;
        }

        var ordered = new List<string>(endpointsToTry.Count);

        for (int i = 0; i < preferredCount; i++)
        {
            var index = (preferredStartIndex + i) % preferredCount;
            ordered.Add(endpointsToTry[index]);
        }

        for (int i = preferredCount; i < endpointsToTry.Count; i++)
        {
            ordered.Add(endpointsToTry[i]);
        }

        return ordered;
    }

    /// <summary>
    /// Updates the endpoint order based on benchmark results (fastest first).
    /// </summary>
    public void SetEndpointOrder(List<string> orderedEndpoints)
    {
        lock (_urlIndexLock)
        {
            // Reorder _apiUrls to match the benchmarked order
            var reordered = orderedEndpoints.Where(e => _apiUrls.Contains(e)).ToList();

            // Add any endpoints that weren't benchmarked (shouldn't happen, but be safe)
            foreach (var url in _apiUrls.Where(u => !reordered.Contains(u)))
            {
                reordered.Add(url);
            }

            _apiUrls.Clear();
            _apiUrls.AddRange(reordered);
            _currentUrlIndex = 0;

            _logger.LogDebug("📊 {Service} endpoints reordered by benchmark: {Endpoints}",
                _serviceName, string.Join(", ", _apiUrls.Take(3)));
        }
    }

    /// <summary>
    /// Tries the request with the next provider in round-robin, then falls back to others on failure.
    /// This distributes load evenly across all providers while maintaining reliability.
    /// Performs quick health checks first to avoid wasting time on dead endpoints.
    /// Throws exception if all endpoints fail.
    /// </summary>
    public async Task<T> TryWithFallbackAsync<T>(Func<string, Task<T>> action)
    {
        if (_apiUrls.Count == 0)
        {
            throw new InvalidOperationException($"No {_serviceName} endpoints are configured");
        }

        // Get healthy endpoints first (with caching to avoid excessive checks)
        var healthyEndpoints = await GetHealthyEndpointsAsync();

        // Try healthy endpoints first, then fall back to all if needed
        var endpointsToTry = healthyEndpoints.Count < _apiUrls.Count
            ? healthyEndpoints.Concat(_apiUrls.Except(healthyEndpoints)).ToList()
            : healthyEndpoints;

        var orderedEndpoints = BuildTryOrder(endpointsToTry);

        // Try preferred fast endpoints first, then full fallback pool.
        for (int attempt = 0; attempt < orderedEndpoints.Count; attempt++)
        {
            var baseUrl = orderedEndpoints[attempt];

            try
            {
                _logger.LogDebug("Trying {Service} endpoint {Endpoint} (attempt {Attempt}/{Total})",
                    _serviceName, baseUrl, attempt + 1, orderedEndpoints.Count);
                return await action(baseUrl);
            }
            catch (Exception ex)
            {
                LogEndpointFailure(baseUrl, ex, willRetry: attempt < orderedEndpoints.Count - 1);

                // Mark as unhealthy in cache
                lock (_healthCacheLock)
                {
                    _healthCache[baseUrl] = (false, DateTime.UtcNow);
                }

                if (attempt == orderedEndpoints.Count - 1)
                {
                    _logger.LogError("All {Count} {Service} endpoints failed", orderedEndpoints.Count, _serviceName);
                    throw;
                }
            }
        }
        throw new Exception($"All {_serviceName} endpoints failed");
    }

    /// <summary>
    /// Races the top N fastest endpoints in parallel and returns the first successful result.
    /// Cancels remaining requests once one succeeds. Used for latency-sensitive operations like search.
    /// </summary>
    public async Task<T> RaceTopEndpointsAsync<T>(int topN, Func<string, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (_apiUrls.Count == 0)
        {
            throw new InvalidOperationException($"No {_serviceName} endpoints are configured");
        }

        if (_apiUrls.Count == 1 || topN <= 1)
        {
            // No point racing with one endpoint - use fallback instead
            return await TryWithFallbackAsync(baseUrl => action(baseUrl, cancellationToken));
        }

        // Get top N fastest healthy endpoints
        var endpointsToRace = _apiUrls.Take(Math.Min(topN, _apiUrls.Count)).ToList();

        if (endpointsToRace.Count == 1)
        {
            return await action(endpointsToRace[0], cancellationToken);
        }

        _logger.LogInformation("Racing {Count} {Service} endpoints: {Endpoints}",
            endpointsToRace.Count, _serviceName, string.Join(", ", endpointsToRace));

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new List<Task<(T result, string endpoint, bool success)>>();

        // Start racing the top N endpoints
        foreach (var baseUrl in endpointsToRace)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("🏁 Racing {Service} endpoint {Endpoint}", _serviceName, baseUrl);
                    var result = await action(baseUrl, raceCts.Token);
                    return (result, baseUrl, true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("{Service} race failed for endpoint {Endpoint}: {Message}", _serviceName, baseUrl, ex.Message);
                    return (default(T)!, baseUrl, false);
                }
            }, raceCts.Token);

            tasks.Add(task);
        }

        // Wait for first successful completion
        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            var (result, endpoint, success) = await completedTask;

            if (success)
            {
                _logger.LogInformation("{Service} race won by {Endpoint}", _serviceName, endpoint);
                raceCts.Cancel(); // Cancel all other requests
                return result;
            }

            tasks.Remove(completedTask);
        }

        _logger.LogError("All raced {Service} endpoints failed: {Endpoints}",
            _serviceName, string.Join(", ", endpointsToRace));
        throw new Exception($"All {topN} {_serviceName} endpoints failed in race");
    }

    /// <summary>
    /// Tries the request with the next provider in round-robin, then falls back to others on failure.
    /// Performs quick health checks first to avoid wasting time on dead endpoints.
    /// Returns default value if all endpoints fail (does not throw).
    /// </summary>
    public async Task<T> TryWithFallbackAsync<T>(Func<string, Task<T>> action, T defaultValue)
    {
        if (_apiUrls.Count == 0)
        {
            _logger.LogWarning("No {Service} endpoints are configured, returning default value", _serviceName);
            return defaultValue;
        }

        // Get healthy endpoints first (with caching to avoid excessive checks)
        var healthyEndpoints = await GetHealthyEndpointsAsync();

        // Try healthy endpoints first, then fall back to all if needed
        var endpointsToTry = healthyEndpoints.Count < _apiUrls.Count
            ? healthyEndpoints.Concat(_apiUrls.Except(healthyEndpoints)).ToList()
            : healthyEndpoints;

        var orderedEndpoints = BuildTryOrder(endpointsToTry);

        // Try preferred fast endpoints first, then full fallback pool.
        for (int attempt = 0; attempt < orderedEndpoints.Count; attempt++)
        {
            var baseUrl = orderedEndpoints[attempt];

            try
            {
                _logger.LogDebug("Trying {Service} endpoint {Endpoint} (attempt {Attempt}/{Total})",
                    _serviceName, baseUrl, attempt + 1, orderedEndpoints.Count);
                return await action(baseUrl);
            }
            catch (Exception ex)
            {
                LogEndpointFailure(baseUrl, ex, willRetry: attempt < orderedEndpoints.Count - 1);

                // Mark as unhealthy in cache
                lock (_healthCacheLock)
                {
                    _healthCache[baseUrl] = (false, DateTime.UtcNow);
                }

                if (attempt == orderedEndpoints.Count - 1)
                {
                    _logger.LogError("All {Count} {Service} endpoints failed, returning default value",
                        orderedEndpoints.Count, _serviceName);
                    return defaultValue;
                }
            }
        }
        return defaultValue;
    }

    /// <summary>
    /// Tries endpoints until one both succeeds and returns an acceptable result.
    /// Unacceptable results continue to the next endpoint without poisoning health state.
    /// </summary>
    public async Task<T> TryWithFallbackAsync<T>(
        Func<string, Task<T>> action,
        Func<T, bool> isAcceptableResult,
        T defaultValue)
    {
        if (isAcceptableResult == null)
        {
            throw new ArgumentNullException(nameof(isAcceptableResult));
        }

        if (_apiUrls.Count == 0)
        {
            _logger.LogWarning("No {Service} endpoints are configured, returning default value", _serviceName);
            return defaultValue;
        }

        // Get healthy endpoints first (with caching to avoid excessive checks)
        var healthyEndpoints = await GetHealthyEndpointsAsync();

        // Try healthy endpoints first, then fall back to all if needed
        var endpointsToTry = healthyEndpoints.Count < _apiUrls.Count
            ? healthyEndpoints.Concat(_apiUrls.Except(healthyEndpoints)).ToList()
            : healthyEndpoints;

        var orderedEndpoints = BuildTryOrder(endpointsToTry);

        for (int attempt = 0; attempt < orderedEndpoints.Count; attempt++)
        {
            var baseUrl = orderedEndpoints[attempt];

            try
            {
                _logger.LogDebug("Trying {Service} endpoint {Endpoint} (attempt {Attempt}/{Total})",
                    _serviceName, baseUrl, attempt + 1, orderedEndpoints.Count);

                var result = await action(baseUrl);
                if (isAcceptableResult(result))
                {
                    return result;
                }

                _logger.LogDebug("{Service} endpoint {Endpoint} returned an unacceptable result, trying next...",
                    _serviceName, baseUrl);

                if (attempt == orderedEndpoints.Count - 1)
                {
                    _logger.LogWarning("All {Count} {Service} endpoints returned unacceptable results, returning default value",
                        orderedEndpoints.Count, _serviceName);
                    return defaultValue;
                }
            }
            catch (Exception ex)
            {
                LogEndpointFailure(baseUrl, ex, willRetry: attempt < orderedEndpoints.Count - 1);

                lock (_healthCacheLock)
                {
                    _healthCache[baseUrl] = (false, DateTime.UtcNow);
                }

                if (attempt == orderedEndpoints.Count - 1)
                {
                    _logger.LogError("All {Count} {Service} endpoints failed, returning default value",
                        orderedEndpoints.Count, _serviceName);
                    return defaultValue;
                }
            }
        }

        return defaultValue;
    }

    private void LogEndpointFailure(string baseUrl, Exception ex, bool willRetry)
    {
        var message = BuildFailureSummary(ex);

        if (willRetry)
        {
            _logger.LogWarning("{Service} request failed at {Endpoint}: {Error}. Trying next...",
                _serviceName, baseUrl, message);
        }
        else
        {
            _logger.LogError("{Service} request failed at {Endpoint}: {Error}",
                _serviceName, baseUrl, message);
        }

        _logger.LogDebug(ex, "{Service} detailed failure for endpoint {Endpoint}",
            _serviceName, baseUrl);
    }

    private static string BuildFailureSummary(Exception ex)
    {
        if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue)
        {
            var statusCode = (int)httpRequestException.StatusCode.Value;
            return $"{statusCode}: {httpRequestException.StatusCode.Value}";
        }

        return ex.Message;
    }

    /// <summary>
    /// Processes multiple items in parallel across all available endpoints.
    /// Each endpoint processes items sequentially. Failed endpoints are blacklisted.
    /// </summary>
    public async Task<List<TResult>> ProcessInParallelAsync<TItem, TResult>(
        List<TItem> items,
        Func<string, TItem, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        if (!items.Any())
        {
            return new List<TResult>();
        }

        if (_apiUrls.Count == 0)
        {
            _logger.LogWarning("No {Service} endpoints are configured, skipping parallel processing", _serviceName);
            return new List<TResult>();
        }

        var results = new List<TResult>();
        var resultsLock = new object();
        var itemQueue = new Queue<TItem>(items);
        var queueLock = new object();
        var blacklistedEndpoints = new HashSet<string>();
        var blacklistLock = new object();

        using var endpointGate = new SemaphoreSlim(MaximumConcurrentEndpointOperations);
        // Every configured endpoint remains eligible, but only a bounded number
        // can actively process or fail over work at once.
        var tasks = _apiUrls.Select(async endpoint =>
        {
            await endpointGate.WaitAsync(cancellationToken);
            try
            {
                while (true)
                {
                    // Check if endpoint is blacklisted
                    lock (blacklistLock)
                    {
                        if (blacklistedEndpoints.Contains(endpoint))
                        {
                            return;
                        }
                    }

                    // Get next item from queue
                    TItem? item;
                    lock (queueLock)
                    {
                        if (itemQueue.Count == 0)
                        {
                            return; // No more items to process
                        }
                        item = itemQueue.Dequeue();
                    }

                    // Process the item
                    try
                    {
                        var result = await action(endpoint, item, cancellationToken);

                        lock (resultsLock)
                        {
                            results.Add(result);
                        }

                        _logger.LogDebug("✓ {Service} endpoint {Endpoint} processed item ({Completed}/{Total})",
                            _serviceName, endpoint, results.Count, items.Count);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "✗ {Service} endpoint {Endpoint} failed, blacklisting",
                            _serviceName, endpoint);

                        // Blacklist this endpoint
                        lock (blacklistLock)
                        {
                            blacklistedEndpoints.Add(endpoint);
                        }

                        // Put item back in queue for another endpoint to try
                        lock (queueLock)
                        {
                            itemQueue.Enqueue(item);
                        }

                        return; // Exit this endpoint's task
                    }
                }
            }
            finally
            {
                endpointGate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        _logger.LogInformation("🏁 {Service} parallel processing complete: {Completed}/{Total} items, {Blacklisted} endpoints blacklisted",
            _serviceName, results.Count, items.Count, blacklistedEndpoints.Count);

        return results;
    }
}
