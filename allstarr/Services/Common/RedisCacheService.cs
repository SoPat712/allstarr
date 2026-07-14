using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using StackExchange.Redis;
using System.Text.Json;
using allstarr.Core.Operations;

namespace allstarr.Services.Common;

public interface IRedisConnectionFactory
{
    IConnectionMultiplexer Connect(string connectionString);
}

public sealed class RedisConnectionFactory : IRedisConnectionFactory
{
    public IConnectionMultiplexer Connect(string connectionString) =>
        ConnectionMultiplexer.Connect(connectionString);
}

/// <summary>
/// Redis caching service for metadata and images.
/// </summary>
public class RedisCacheService
{
    private readonly RedisSettings _settings;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly OperationalRuntimeState? _runtimeState;
    private readonly IRedisConnectionFactory _connectionFactory;
    private readonly IPlatformClock _clock;
    private IConnectionMultiplexer? _redis;
    private IDatabase? _db;
    private readonly object _lock = new();
    private DateTimeOffset _nextReconnectAttempt = DateTimeOffset.MinValue;

    public RedisCacheService(
        IOptions<RedisSettings> settings,
        ILogger<RedisCacheService> logger)
        : this(
            settings,
            logger,
            null,
            new RedisConnectionFactory(),
            new SystemPlatformClock())
    {
    }

    public RedisCacheService(
        IOptions<RedisSettings> settings,
        ILogger<RedisCacheService> logger,
        OperationalRuntimeState? runtimeState)
        : this(
            settings,
            logger,
            runtimeState,
            new RedisConnectionFactory(),
            new SystemPlatformClock())
    {
    }

    public RedisCacheService(
        IOptions<RedisSettings> settings,
        ILogger<RedisCacheService> logger,
        OperationalRuntimeState? runtimeState,
        IRedisConnectionFactory connectionFactory,
        IPlatformClock clock)
    {
        _settings = settings.Value;
        _logger = logger;
        _runtimeState = runtimeState;
        _connectionFactory = connectionFactory;
        _clock = clock;
        _runtimeState?.RecordValkey(_settings.Enabled, available: false);

        if (_settings.Enabled)
        {
            InitializeConnection();
        }
    }

    private void InitializeConnection()
    {
        try
        {
            _redis = _connectionFactory.Connect(_settings.ConnectionString);
            _db = _redis.GetDatabase();
            _nextReconnectAttempt = DateTimeOffset.MinValue;
            _runtimeState?.RecordValkey(configured: true, available: true);
            _logger.LogInformation("Valkey cache connected using {ConnectionString}", _settings.ConnectionString);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Valkey cache connection failed; cache operations will retry on a bounded interval.");
            _redis = null;
            _db = null;
            _nextReconnectAttempt = _clock.UtcNow.AddSeconds(
                Math.Clamp(_settings.ReconnectIntervalSeconds, 1, 3600));
            _runtimeState?.RecordValkey(_settings.Enabled, available: false);
        }
    }

    public bool IsEnabled
    {
        get
        {
            if (!_settings.Enabled)
            {
                _runtimeState?.RecordValkey(configured: false, available: false);
                return false;
            }

            var available = _db != null && (_redis?.IsConnected ?? false);
            if (!available && _clock.UtcNow >= _nextReconnectAttempt)
            {
                available = TryReconnect();
            }

            _runtimeState?.RecordValkey(_settings.Enabled, available);
            return available;
        }
    }

    /// <summary>
    /// Gets a cached value as a string.
    /// </summary>
    public async Task<string?> GetStringAsync(string key)
    {
        if (!IsEnabled) return null;

        try
        {
            var value = await _db!.StringGetAsync(key);

            if (value.HasValue)
            {
                _logger.LogDebug("Redis cache HIT: {Key}", key);
            }
            else
            {
                _logger.LogDebug("Redis cache MISS: {Key}", key);
            }
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis GET failed for key: {Key}", key);
            return null;
        }
    }

    /// <summary>
    /// Gets a cached value and deserializes it.
    /// </summary>
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var json = await GetStringAsync(key);
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize cached value for key: {Key}", key);
            return null;
        }
    }

    /// <summary>
    /// Sets a cached value with TTL.
    /// </summary>
    public async Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (!IsEnabled) return false;

        try
        {
            return await SetStringInternalAsync(key, value, expiry);
        }
        catch (RedisTimeoutException ex)
        {
            return await RetrySetAfterReconnectAsync(
                key,
                value,
                expiry,
                ex,
                "Redis SET timeout for key: {Key}. Reconnecting and retrying once.");
        }
        catch (RedisConnectionException ex)
        {
            return await RetrySetAfterReconnectAsync(
                key,
                value,
                expiry,
                ex,
                "Redis SET connection error for key: {Key}. Reconnecting and retrying once.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis SET failed for key: {Key}", key);
            return false;
        }
    }

    private async Task<bool> SetStringInternalAsync(string key, string value, TimeSpan? expiry)
    {
        var result = await _db!.StringSetAsync(key, value, expiry);
        if (result)
        {
            _logger.LogDebug("Redis cache SET: {Key} (TTL: {Expiry})", key, expiry?.ToString() ?? "none");
        }
        else
        {
            _logger.LogWarning("Redis SET returned false for key: {Key}", key);
        }
        return result;
    }

    private async Task<bool> RetrySetAfterReconnectAsync(
        string key,
        string value,
        TimeSpan? expiry,
        Exception ex,
        string warningMessage)
    {
        _logger.LogWarning(ex, warningMessage, key);

        if (!TryReconnect(force: true))
        {
            _logger.LogError("Redis reconnect failed; cannot retry SET for key: {Key}", key);
            return false;
        }

        try
        {
            return await SetStringInternalAsync(key, value, expiry);
        }
        catch (Exception retryEx)
        {
            _logger.LogError(retryEx, "Redis SET retry failed for key: {Key}", key);
            return false;
        }
    }

    private bool TryReconnect(bool force = false)
    {
        lock (_lock)
        {
            if (!_settings.Enabled)
            {
                return false;
            }

            if (_db != null && (_redis?.IsConnected ?? false))
            {
                return true;
            }

            if (!force && _clock.UtcNow < _nextReconnectAttempt)
            {
                return false;
            }

            try
            {
                _redis?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing Redis connection during reconnect");
            }

            _redis = null;
            _db = null;
            InitializeConnection();
            return _db != null && (_redis?.IsConnected ?? false);
        }
    }

    /// <summary>
    /// Sets a cached value by serializing it with TTL.
    /// </summary>
    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
    {
        try
        {
            var json = JsonSerializer.Serialize(value);
            return await SetStringAsync(key, json, expiry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize value for key: {Key}", key);
            return false;
        }
    }

    /// <summary>
    /// Deletes a cached value.
    /// </summary>
    public async Task<bool> DeleteAsync(string key)
    {
        if (!IsEnabled) return false;

        try
        {
            return await _db!.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis DELETE failed for key: {Key}", key);
            return false;
        }
    }

    /// <summary>
    /// Checks if a key exists.
    /// </summary>
    public async Task<bool> ExistsAsync(string key)
    {
        if (!IsEnabled) return false;

        try
        {
            return await _db!.KeyExistsAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis EXISTS failed for key: {Key}", key);
            return false;
        }
    }

    /// <summary>
    /// Gets all keys matching a pattern.
    /// </summary>
    public IEnumerable<string> GetKeysByPattern(string pattern)
    {
        if (!IsEnabled) return Array.Empty<string>();

        try
        {
            var server = _redis!.GetServer(_redis.GetEndPoints().First());
            return server.Keys(pattern: pattern).Select(k => (string)k!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis GET KEYS BY PATTERN failed for pattern: {Pattern}", pattern);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Deletes all keys matching a pattern (e.g., "search:*").
    /// WARNING: Use with caution as this scans all keys.
    /// </summary>
    public async Task<int> DeleteByPatternAsync(string pattern)
    {
        if (!IsEnabled) return 0;

        try
        {
            var server = _redis!.GetServer(_redis.GetEndPoints().First());
            var keys = server.Keys(pattern: pattern).ToArray();

            if (keys.Length == 0)
            {
                _logger.LogDebug("No keys found matching pattern: {Pattern}", pattern);
                return 0;
            }

            var deleted = await _db!.KeyDeleteAsync(keys);
            _logger.LogDebug("Deleted {Count} Redis keys matching pattern: {Pattern}", deleted, pattern);
            return (int)deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis DELETE BY PATTERN failed for pattern: {Pattern}", pattern);
            return 0;
        }
    }
}
