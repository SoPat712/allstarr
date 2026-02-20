namespace allstarr.Services.Common;

/// <summary>
/// Helper for round-robin load balancing with fallback across multiple API endpoints.
/// Distributes load evenly while maintaining reliability through automatic failover.
/// </summary>
public class RoundRobinFallbackHelper
{
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
        
        if (_apiUrls.Count == 0)
        {
            throw new ArgumentException("API URLs list cannot be empty", nameof(apiUrls));
        }
        
        // Create a dedicated HttpClient for health checks with short timeout
        _healthCheckClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3) // Quick health check timeout
        };
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
        var healthCheckTasks = _apiUrls.Select(async url => new
        {
            Url = url,
            IsHealthy = await IsEndpointHealthyAsync(url)
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
        // Get healthy endpoints first (with caching to avoid excessive checks)
        var healthyEndpoints = await GetHealthyEndpointsAsync();
        
        // Start with the next URL in round-robin to distribute load
        var startIndex = 0;
        lock (_urlIndexLock)
        {
            startIndex = _currentUrlIndex;
            _currentUrlIndex = (_currentUrlIndex + 1) % _apiUrls.Count;
        }
        
        // Try healthy endpoints first, then fall back to all if needed
        var endpointsToTry = healthyEndpoints.Count < _apiUrls.Count 
            ? healthyEndpoints.Concat(_apiUrls.Except(healthyEndpoints)).ToList()
            : healthyEndpoints;
        
        // Try all URLs starting from the round-robin selected one
        for (int attempt = 0; attempt < endpointsToTry.Count; attempt++)
        {
            var urlIndex = (startIndex + attempt) % endpointsToTry.Count;
            var baseUrl = endpointsToTry[urlIndex];
            
            try
            {
                _logger.LogDebug("Trying {Service} endpoint {Endpoint} (attempt {Attempt}/{Total})", 
                    _serviceName, baseUrl, attempt + 1, endpointsToTry.Count);
                return await action(baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Service} request failed with endpoint {Endpoint}, trying next...", 
                    _serviceName, baseUrl);
                
                // Mark as unhealthy in cache
                lock (_healthCacheLock)
                {
                    _healthCache[baseUrl] = (false, DateTime.UtcNow);
                }
                
                if (attempt == endpointsToTry.Count - 1)
                {
                    _logger.LogError("All {Count} {Service} endpoints failed", endpointsToTry.Count, _serviceName);
                    throw;
                }
            }
        }
        throw new Exception($"All {_serviceName} endpoints failed");
    }

    /// <summary>
    /// Races all endpoints in parallel and returns the first successful result.
    /// Cancels remaining requests once one succeeds. Great for latency-sensitive operations.
    /// </summary>
    /// <summary>
        /// Races the top N fastest endpoints in parallel and returns the first successful result.
        /// Cancels remaining requests once one succeeds. Used for latency-sensitive operations like search.
        /// </summary>
        public async Task<T> RaceTopEndpointsAsync<T>(int topN, Func<string, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
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
                    _logger.LogDebug("🏆 {Service} race won by {Endpoint}, canceling others", _serviceName, endpoint);
                    raceCts.Cancel(); // Cancel all other requests
                    return result;
                }

                tasks.Remove(completedTask);
            }

            throw new Exception($"All {topN} {_serviceName} endpoints failed in race");
        }

    /// <summary>
    /// Tries the request with the next provider in round-robin, then falls back to others on failure.
    /// Performs quick health checks first to avoid wasting time on dead endpoints.
    /// Returns default value if all endpoints fail (does not throw).
    /// </summary>
    public async Task<T> TryWithFallbackAsync<T>(Func<string, Task<T>> action, T defaultValue)
    {
        // Get healthy endpoints first (with caching to avoid excessive checks)
        var healthyEndpoints = await GetHealthyEndpointsAsync();
        
        // Start with the next URL in round-robin to distribute load
        var startIndex = 0;
        lock (_urlIndexLock)
        {
            startIndex = _currentUrlIndex;
            _currentUrlIndex = (_currentUrlIndex + 1) % _apiUrls.Count;
        }
        
        // Try healthy endpoints first, then fall back to all if needed
        var endpointsToTry = healthyEndpoints.Count < _apiUrls.Count 
            ? healthyEndpoints.Concat(_apiUrls.Except(healthyEndpoints)).ToList()
            : healthyEndpoints;
        
        // Try all URLs starting from the round-robin selected one
        for (int attempt = 0; attempt < endpointsToTry.Count; attempt++)
        {
            var urlIndex = (startIndex + attempt) % endpointsToTry.Count;
            var baseUrl = endpointsToTry[urlIndex];
            
            try
            {
                _logger.LogDebug("Trying {Service} endpoint {Endpoint} (attempt {Attempt}/{Total})", 
                    _serviceName, baseUrl, attempt + 1, endpointsToTry.Count);
                return await action(baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Service} request failed with endpoint {Endpoint}, trying next...", 
                    _serviceName, baseUrl);
                
                // Mark as unhealthy in cache
                lock (_healthCacheLock)
                {
                    _healthCache[baseUrl] = (false, DateTime.UtcNow);
                }
                
                if (attempt == endpointsToTry.Count - 1)
                {
                    _logger.LogError("All {Count} {Service} endpoints failed, returning default value", 
                        endpointsToTry.Count, _serviceName);
                    return defaultValue;
                }
            }
        }
        return defaultValue;
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

        var results = new List<TResult>();
        var resultsLock = new object();
        var itemQueue = new Queue<TItem>(items);
        var queueLock = new object();
        var blacklistedEndpoints = new HashSet<string>();
        var blacklistLock = new object();

        // Start one task per endpoint
        var tasks = _apiUrls.Select(async endpoint =>
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
        }).ToList();

        await Task.WhenAll(tasks);

        _logger.LogInformation("🏁 {Service} parallel processing complete: {Completed}/{Total} items, {Blacklisted} endpoints blacklisted",
            _serviceName, results.Count, items.Count, blacklistedEndpoints.Count);

        return results;
    }
}
