using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using StackExchange.Redis;
using System.Text.Json;

namespace allstarr.Services.Common;

/// <summary>
/// Redis caching service for metadata and images.
/// </summary>
public class RedisCacheService
{
    private readonly RedisSettings _settings;
    private readonly ILogger<RedisCacheService> _logger;
    private IConnectionMultiplexer? _redis;
    private IDatabase? _db;
    private readonly object _lock = new();

    public RedisCacheService(
        IOptions<RedisSettings> settings,
        ILogger<RedisCacheService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        if (_settings.Enabled)
        {
            InitializeConnection();
        }
    }

    private void InitializeConnection()
    {
        try
        {
            _redis = ConnectionMultiplexer.Connect(_settings.ConnectionString);
            _db = _redis.GetDatabase();
            _logger.LogInformation("Redis connected: {ConnectionString}", _settings.ConnectionString);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis connection failed. Caching disabled.");
            _redis = null;
            _db = null;
        }
    }

    public bool IsEnabled => _settings.Enabled && _db != null;

    /// <summary>
    /// Gets a cached value as a string.
    /// </summary>
    public async Task<string?> GetStringAsync(string key)
    {
        if (!IsEnabled) return null;

        try
        {
            return await _db!.StringGetAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis GET failed for key: {Key}", key);
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
            _logger.LogWarning(ex, "Failed to deserialize cached value for key: {Key}", key);
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
            return await _db!.StringSetAsync(key, value, expiry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis SET failed for key: {Key}", key);
            return false;
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
            _logger.LogWarning(ex, "Failed to serialize value for key: {Key}", key);
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
            _logger.LogWarning(ex, "Redis DELETE failed for key: {Key}", key);
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
            _logger.LogWarning(ex, "Redis EXISTS failed for key: {Key}", key);
            return false;
        }
    }
}
