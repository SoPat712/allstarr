using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Controllers;

namespace allstarr.Services.Common;

/// <summary>
/// Background service that periodically cleans up old cached files
/// Only runs when StorageMode is set to Cache
/// </summary>
public class CacheCleanupService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CacheCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public CacheCleanupService(
        IConfiguration configuration,
        IOptions<SubsonicSettings> subsonicSettings,
        IServiceProvider serviceProvider,
        ILogger<CacheCleanupService> logger)
    {
        _configuration = configuration;
        _subsonicSettings = subsonicSettings.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only run if storage mode is Cache
        if (_subsonicSettings.StorageMode != StorageMode.Cache)
        {
            _logger.LogInformation("CacheCleanupService disabled: StorageMode is not Cache");
            return;
        }

        _logger.LogInformation("CacheCleanupService started with cleanup interval of {Interval} and retention of {Hours} hours",
            _cleanupInterval, _subsonicSettings.CacheDurationHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldCachedFilesAsync(stoppingToken);
                await CleanupTranscodedCacheAsync(stoppingToken);
                await ProcessPendingDeletionsAsync(stoppingToken);
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
                // Continue running even if cleanup fails
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
        }

        _logger.LogInformation("CacheCleanupService stopped");
    }

    private async Task CleanupOldCachedFilesAsync(CancellationToken cancellationToken)
    {
        // Get the actual cache path used by download services
        var downloadPath = _configuration["Library:DownloadPath"] ?? "downloads";
        var cachePath = Path.Combine(downloadPath, "cache");

        if (!Directory.Exists(cachePath))
        {
            _logger.LogDebug("Cache directory does not exist: {Path}", cachePath);
            return;
        }

        var cutoffTime = DateTime.UtcNow.AddHours(-_subsonicSettings.CacheDurationHours);
        var deletedCount = 0;
        var totalSize = 0L;

        _logger.LogInformation("Starting cache cleanup: deleting files older than {CutoffTime} from {Path}", cutoffTime, cachePath);

        try
        {
            // Get all files in cache directory and subdirectories
            var files = Directory.GetFiles(cachePath, "*.*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var fileInfo = new FileInfo(filePath);

                    // Use last write time (when file was created/downloaded) to determine if file should be deleted
                    // LastAccessTime is unreliable on many filesystems (noatime mount option)
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        var size = fileInfo.Length;
                        File.Delete(filePath);
                        deletedCount++;
                        totalSize += size;
                        _logger.LogDebug("Deleted cached file: {Path} (age: {Age:F1} hours)",
                            filePath, (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalHours);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete cached file: {Path}", filePath);
                }
            }

            // Clean up empty directories
            await CleanupEmptyDirectoriesAsync(cachePath, cancellationToken);

            if (deletedCount > 0)
            {
                var sizeMB = totalSize / (1024.0 * 1024.0);
                _logger.LogInformation("Cache cleanup completed: deleted {Count} files, freed {Size:F2} MB",
                    deletedCount, sizeMB);
            }
            else
            {
                _logger.LogInformation("Cache cleanup completed: no files to delete");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }
    }

    /// <summary>
    /// Cleans up transcoded quality-override files based on CACHE_TRANSCODE_MINUTES TTL.
    /// This always runs regardless of StorageMode, since transcoded files are a separate concern.
    /// </summary>
    private async Task CleanupTranscodedCacheAsync(CancellationToken cancellationToken)
    {
        var downloadPath = _configuration["Library:DownloadPath"] ?? "downloads";
        var transcodedPath = Path.Combine(downloadPath, "transcoded");

        if (!Directory.Exists(transcodedPath))
        {
            return;
        }

        var ttl = CacheExtensions.TranscodeCacheTTL;
        var cutoffTime = DateTime.UtcNow - ttl;
        var deletedCount = 0;
        var totalSize = 0L;

        try
        {
            var files = Directory.GetFiles(transcodedPath, "*.*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var fileInfo = new FileInfo(filePath);

                    // Use last write time (updated on cache hit) to determine if file should be deleted
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        var size = fileInfo.Length;
                        File.Delete(filePath);
                        deletedCount++;
                        totalSize += size;
                        _logger.LogDebug("Deleted transcoded cache file: {Path} (age: {Age:F1} minutes)",
                            filePath, (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalMinutes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete transcoded cache file: {Path}", filePath);
                }
            }

            // Clean up empty directories in the transcoded folder
            await CleanupEmptyDirectoriesAsync(transcodedPath, cancellationToken);

            if (deletedCount > 0)
            {
                var sizeMB = totalSize / (1024.0 * 1024.0);
                _logger.LogInformation("Transcoded cache cleanup: deleted {Count} files, freed {Size:F2} MB (TTL: {TTL} minutes)",
                    deletedCount, sizeMB, ttl.TotalMinutes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during transcoded cache cleanup");
        }
    }

    private async Task CleanupEmptyDirectoriesAsync(string rootPath, CancellationToken cancellationToken)
    {
        try
        {
            var directories = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length); // Process deepest directories first

            foreach (var directory in directories)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                        _logger.LogDebug("Deleted empty directory: {Path}", directory);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete empty directory: {Path}", directory);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up empty directories");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Processes pending track deletions from the kept folder.
    /// </summary>
    private async Task ProcessPendingDeletionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Create a scope to get the JellyfinController
            using var scope = _serviceProvider.CreateScope();
            var jellyfinController = scope.ServiceProvider.GetService<JellyfinController>();
            
            if (jellyfinController != null)
            {
                await jellyfinController.ProcessPendingDeletionsAsync();
            }
            else
            {
                _logger.LogWarning("Could not resolve JellyfinController for pending deletions processing");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pending deletions");
        }
    }
}
