using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Services.Common;

/// <summary>
/// Background service that periodically cleans up old cached files
/// Only runs when StorageMode is set to Cache
/// </summary>
public class CacheCleanupService : BackgroundService
{
    private readonly allstarr.Core.Operations.BackgroundTaskActivity? _taskActivity;
    private readonly ManagedTrackCacheService _audio;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly ILogger<CacheCleanupService> _logger;
    private bool _cleanupFailed;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public CacheCleanupService(
        ManagedTrackCacheService audio,
        IOptions<SubsonicSettings> subsonicSettings,
        ILogger<CacheCleanupService> logger, allstarr.Core.Operations.BackgroundTaskActivity? taskActivity = null)
    {
        _audio = audio;
        _taskActivity = taskActivity;
        _subsonicSettings = subsonicSettings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CacheCleanupService started with cleanup interval of {Interval}",
            _cleanupInterval);

        _taskActivity?.Register("audio-cache-cleanup", "Temporary audio cleanup", _cleanupInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _cleanupFailed = false;
                _taskActivity?.Started("audio-cache-cleanup");
                if (_subsonicSettings.StorageMode == StorageMode.Cache)
                {
                    await CleanupOldCachedFilesAsync(stoppingToken);
                }
                if (_taskActivity != null) await _taskActivity.CompletedAsync("audio-cache-cleanup", !_cleanupFailed, stoppingToken);
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is stopping, exit gracefully
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup");
                if (_taskActivity != null) await _taskActivity.CompletedAsync("audio-cache-cleanup", false, stoppingToken);
                // Continue running even if cleanup fails
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
        }

        _logger.LogInformation("CacheCleanupService stopped");
    }

    internal Task CleanupOldCachedFilesAsync(CancellationToken cancellationToken) =>
        _audio.CleanupExpiredAudioAsync(TimeSpan.FromHours(_subsonicSettings.CacheDurationHours), cancellationToken);
}
