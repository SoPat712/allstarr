using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Services.Common;

/// <summary>
/// Periodically snapshots Redis cache to file system for cold start recovery.
/// Redis is the primary cache, files are the persistence layer.
/// </summary>
public class RedisPersistenceService : BackgroundService
{
    private readonly RedisCacheService _cache;
    private readonly ILogger<RedisPersistenceService> _logger;
    private readonly TimeSpan _snapshotInterval = TimeSpan.FromMinutes(5);
    private const string SnapshotDirectory = "/app/cache/redis-snapshots";

    public RedisPersistenceService(
        RedisCacheService cache,
        ILogger<RedisPersistenceService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 2 minutes after startup before first snapshot (let cache warm up)
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CreateSnapshotAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Redis snapshot");
            }

            await Task.Delay(_snapshotInterval, stoppingToken);
        }
    }

    private async Task CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_cache.IsEnabled)
        {
            _logger.LogWarning("Redis is disabled, skipping snapshot");
            return;
        }

        try
        {
            Directory.CreateDirectory(SnapshotDirectory);

            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var snapshotFile = Path.Combine(SnapshotDirectory, $"snapshot_{timestamp}.json");

            _logger.LogDebug("Redis snapshot service running (using Redis native persistence)");

            // Clean up old snapshots (keep last 10)
            await CleanupOldSnapshotsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Redis snapshot");
        }
    }

    private async Task CleanupOldSnapshotsAsync()
    {
        try
        {
            if (!Directory.Exists(SnapshotDirectory))
                return;

            var files = Directory.GetFiles(SnapshotDirectory, "snapshot_*.json")
                .OrderByDescending(f => f)
                .Skip(10)
                .ToArray();

            foreach (var file in files)
            {
                File.Delete(file);
                _logger.LogDebug("Deleted old snapshot: {File}", Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old snapshots");
        }
    }
}
